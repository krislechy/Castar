#include <dirent.h>
#include <dlfcn.h>
#include <mach-o/dyld.h>
#include <mach-o/loader.h>
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/param.h>

// ABI from dotnet/runtime v11.0.0, src/native/corehost/host_runtime_contract.h (MIT).
struct native_code_context {
    size_t size;
    const char *assembly_path;
    const char *owner_composite_name;
};

struct native_code_data {
    size_t size;
    void *header;
    size_t image_size;
    void *image_base;
};

struct runtime_contract {
    size_t size;
    void *context;
    size_t (*get_runtime_property)(const char *, char *, size_t, void *);
    bool (*bundle_probe)(const char *, int64_t *, int64_t *, int64_t *);
    const void *(*pinvoke_override)(const char *, const char *);
    bool (*external_assembly_probe)(const char *, void **, int64_t *);
    bool (*get_native_code_data)(const struct native_code_context *, struct native_code_data *);
};

typedef int (*initialize_runtime)(const char *, const char *, int, const char **, const char **,
                                  void **, unsigned int *);
typedef int (*execute_assembly)(void *, unsigned int, int, const char **, const char *, unsigned int *);
typedef int (*shutdown_runtime)(void *, unsigned int);

static const char composite_name[] = "Microsoft.NETCore.App.r2r.dylib";
static struct native_code_data composite;

static bool get_native_code(const struct native_code_context *context, struct native_code_data *data)
{
    if (context->size < sizeof(*context) || data->size < sizeof(*data) ||
        !context->owner_composite_name || strcmp(context->owner_composite_name, composite_name) != 0)
        return false;

    data->header = composite.header;
    data->image_size = composite.image_size;
    data->image_base = composite.image_base;
    return true;
}

static const struct runtime_contract contract = {
    .size = sizeof(contract),
    .get_native_code_data = get_native_code
};

static bool make_path(char *path, const char *directory, const char *file)
{
    const int written = snprintf(path, MAXPATHLEN, "%s/%s", directory, file);
    return written >= 0 && written < MAXPATHLEN;
}

static bool load_composite(const char *directory)
{
    char path[MAXPATHLEN];
    if (!make_path(path, directory, composite_name))
        return false;

    void *image = dlopen(path, RTLD_NOW | RTLD_GLOBAL);
    if (!image) {
        fprintf(stderr, "Cannot load CoreCLR composite: %s\n", dlerror());
        return false;
    }

    composite.header = dlsym(image, "RTR_HEADER");
    Dl_info info;
    if (!composite.header || !dladdr(composite.header, &info))
        return false;

    composite.image_base = info.dli_fbase;
    const struct mach_header_64 *header = composite.image_base;
    if (header->magic != MH_MAGIC_64)
        return false;

    const char *position = (const char *)(header + 1);
    const char *end = position + header->sizeofcmds;
    for (uint32_t index = 0; index < header->ncmds && end - position >= (ptrdiff_t)sizeof(struct load_command); index++) {
        const struct load_command *command = (const struct load_command *)position;
        if (command->cmdsize < sizeof(*command) || command->cmdsize > (size_t)(end - position))
            return false;
        if (command->cmd == LC_SEGMENT_64 && command->cmdsize >= sizeof(struct segment_command_64)) {
            const struct segment_command_64 *segment = (const struct segment_command_64 *)position;
            const uint64_t upper_bound = segment->vmaddr + segment->vmsize;
            if (upper_bound > composite.image_size)
                composite.image_size = upper_bound;
        }
        position += command->cmdsize;
    }

    return composite.image_size != 0;
}

static char *trusted_assemblies(const char *directory)
{
    DIR *listing = opendir(directory);
    if (!listing)
        return NULL;

    char *paths = NULL;
    size_t used = 0;
    size_t capacity = 0;
    const struct dirent *entry;
    while ((entry = readdir(listing))) {
        const size_t name_length = strlen(entry->d_name);
        if (name_length < 4 || strcmp(entry->d_name + name_length - 4, ".dll") != 0)
            continue;

        const size_t length = strlen(directory) + 1 + name_length + 1;
        if (used + length + 1 > capacity) {
            const size_t next_capacity = (used + length + 1) * 2;
            char *next = realloc(paths, next_capacity);
            if (!next) {
                free(paths);
                paths = NULL;
                break;
            }
            paths = next;
            capacity = next_capacity;
        }
        used += (size_t)snprintf(paths + used, capacity - used, "%s/%s:", directory, entry->d_name);
    }
    closedir(listing);
    return paths;
}

int main(int argc, char **argv)
{
    char executable[MAXPATHLEN];
    char bundle[MAXPATHLEN];
    uint32_t size = sizeof(executable);
    if (_NSGetExecutablePath(executable, &size) != 0 || !realpath(executable, bundle))
        return EXIT_FAILURE;

    char *filename = strrchr(bundle, '/');
    if (!filename)
        return EXIT_FAILURE;
    *filename = '\0';

    char library[MAXPATHLEN], assembly[MAXPATHLEN], directory[MAXPATHLEN];
    if (!make_path(directory, bundle, "lib") || !make_path(library, directory, "libcoreclr.dylib") ||
        !make_path(assembly, bundle, "Castar.Server.dll"))
        return EXIT_FAILURE;

    if (!load_composite(directory)) {
        fputs("Cannot initialize the CoreCLR composite image.\n", stderr);
        return EXIT_FAILURE;
    }

    void *runtime = dlopen(library, RTLD_NOW | RTLD_GLOBAL);
    if (!runtime) {
        fprintf(stderr, "Cannot load CoreCLR: %s\n", dlerror());
        return EXIT_FAILURE;
    }

    initialize_runtime initialize = (initialize_runtime)dlsym(runtime, "coreclr_initialize");
    execute_assembly execute = (execute_assembly)dlsym(runtime, "coreclr_execute_assembly");
    shutdown_runtime shutdown = (shutdown_runtime)dlsym(runtime, "coreclr_shutdown");
    char *paths = trusted_assemblies(directory);
    if (!initialize || !execute || !shutdown || !paths) {
        free(paths);
        fputs("Cannot resolve the CoreCLR host functions or managed assemblies.\n", stderr);
        return EXIT_FAILURE;
    }

    char contract_address[32], base_path[MAXPATHLEN];
    snprintf(contract_address, sizeof(contract_address), "%p", (const void *)&contract);
    if (!make_path(base_path, bundle, "")) {
        free(paths);
        return EXIT_FAILURE;
    }
    const char *keys[] = {
        "HOST_RUNTIME_CONTRACT", "TRUSTED_PLATFORM_ASSEMBLIES", "APP_PATHS",
        "APP_CONTEXT_BASE_DIRECTORY", "NATIVE_DLL_SEARCH_DIRECTORIES", "RUNTIME_IDENTIFIER"
    };
    const char *values[] = {contract_address, paths, directory, base_path, directory, "ios-arm64"};
    void *instance = NULL;
    unsigned int domain = 0, exit_code = EXIT_FAILURE;
    int result = initialize(executable, "Castar", (int)(sizeof(keys) / sizeof(keys[0])), keys, values,
                            &instance, &domain);
    if (result >= 0) {
        result = execute(instance, domain, argc - 1, (const char **)(argv + 1), assembly, &exit_code);
        shutdown(instance, domain);
    }
    free(paths);
    if (result < 0) {
        fprintf(stderr, "CoreCLR returned 0x%08x.\n", (unsigned int)result);
        return EXIT_FAILURE;
    }
    return (int)exit_code;
}
