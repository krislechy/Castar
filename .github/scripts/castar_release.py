"""Prepare Castar releases without rebuilding unchanged server packages."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import plistlib
import re
import subprocess
import tarfile
import tempfile
import xml.etree.ElementTree as ET
import zipfile


CLIENT = ("Castar-win-x64.exe", "Castar-Update-win-x64.zip")
DESKTOP = ("Castar-Server-win-x64.zip", "Castar-Server-linux-x64.tar.gz", "Castar-Server-linux-arm64.tar.gz")
IPHONE = ("Castar-Server-ios-arm64.tar", "Castar-Control-ios-arm64.zip")
SERVER = DESKTOP + IPHONE
CLIENT_DIRECTORIES = {"Castar", "Castar.Updater", "Castar.ReleaseUpdates", "Castar.Logger", "Castar.TrayNotify.Windows"}
DOCS = {"PLAN.md", "README.md", "AGENTS.md"}
TAG = re.compile(r"v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
DIGEST = re.compile(r"sha256:[a-f0-9]{64}")


def capture(*args):
    return subprocess.check_output(args, text=True, encoding="utf-8").strip()


def run(*args):
    subprocess.run(args, check=True)


def version(tag):
    if not TAG.fullmatch(tag):
        raise ValueError(f"Invalid version tag: {tag}")
    return tuple(map(int, tag[1:].split(".")))


def project_version(project):
    value = ET.parse(project).findtext("./PropertyGroup/Version")
    tag = f"v{value}"
    version(tag)
    return tag


def referenced_directories(project):
    """Protect project references even if a formerly client-only project becomes shared."""
    pending = [Path(project).resolve()]
    root = Path.cwd().resolve()
    visited = set()
    while pending:
        current = pending.pop()
        if current in visited:
            continue
        visited.add(current)
        for ref in ET.parse(current).iter("ProjectReference"):
            pending.append((current.parent / ref.attrib["Include"].replace("\\", "/")).resolve())
    return {project.relative_to(root).parts[0] for project in visited}


def server_changes(base, tag):
    client_directories = CLIENT_DIRECTORIES - referenced_directories("Castar.Server/Castar.Server.csproj")
    changes = capture("git", "diff", "--name-only", "--no-renames", "-z", base, tag).split("\0")
    return [path for path in changes if path and path not in DOCS
            and PurePosixPath(path).parts[0] not in client_directories]


def release(tag):
    result = json.loads(capture("gh", "release", "view", tag, "--json", "tagName,isDraft,isPrerelease,assets"))
    if result["tagName"] != tag or result["isPrerelease"]:
        raise ValueError(f"Unexpected release: {tag}")
    return result


def verified_assets(record, names):
    entries = {entry["name"]: entry for entry in record["assets"]}
    for name in names:
        entry = entries.get(name, {})
        if entry.get("state") != "uploaded" or entry.get("size", 0) <= 0 or not DIGEST.fullmatch(entry.get("digest") or ""):
            raise ValueError(f"Missing asset metadata or digest: {name}")
    return {name: entries[name] for name in names}


def asset_label(name, server_tag):
    return f"{name} (server-{server_tag})"


def source_assets(server_tag):
    record = release(server_tag)
    if record["isDraft"]:
        raise ValueError("The previous server release must already be public.")
    entries = verified_assets(record, SERVER)
    if any(entry.get("label") != asset_label(name, server_tag) for name, entry in entries.items()):
        raise ValueError("Server reuse requires a previous full release with versioned server asset labels.")
    return entries


def plan(tag, output):
    if project_version("Castar/Castar.csproj") != tag:
        raise ValueError("The release tag must match the Windows client version.")
    server_tag = project_version("Castar.Server/Castar.Server.csproj")
    with Path("Castar.Server.iOS/App/Info.plist").open("rb") as file:
        iphone_version = plistlib.load(file)["CFBundleShortVersionString"]
    if f"v{iphone_version}" != server_tag or version(server_tag) > version(tag):
        raise ValueError("Server and iPhone versions must agree and cannot exceed the client version.")

    reuse = server_tag != tag
    if reuse:
        run("git", "merge-base", "--is-ancestor", server_tag, tag)
        changed = server_changes(server_tag, tag)
        if changed:
            raise ValueError("Server inputs changed. Bump server and iPhone versions for this release:\n"
                             + "\n".join(changed))
        source_assets(server_tag)
    print(f"{tag}: {'reusing' if reuse else 'building'} server packages {server_tag}")
    with output.open("a", encoding="utf-8") as file:
        file.write(f"server_tag={server_tag}\nbuild_server={str(not reuse).lower()}\n")


def checksum(path):
    with path.open("rb") as file:
        digest = hashlib.file_digest(file, "sha256").hexdigest()
    return (path.stat().st_size, f"sha256:{digest}")


def metadata_checksum(entry):
    return (entry["size"], entry["digest"])


def verify_remote(tag, expected, server_tag):
    entries = verified_assets(release(tag), expected)
    for name, digest in expected.items():
        if metadata_checksum(entries[name]) != digest:
            raise ValueError(f"Uploaded checksum mismatch: {name}")
        if name in SERVER and entries[name].get("label") != asset_label(name, server_tag):
            raise ValueError(f"Uploaded server version label mismatch: {name}")


def upload_missing(tag, directory, expected, server_tag):
    current = release(tag)
    if not current["isDraft"]:
        verify_remote(tag, expected, server_tag)
        return
    attached = {entry["name"] for entry in current["assets"]}
    verify_remote(tag, {name: digest for name, digest in expected.items() if name in attached}, server_tag)
    paths = [str(directory / name) + (f"#{asset_label(name, server_tag)}" if name in SERVER else "")
             for name in expected if name not in attached]
    if paths:
        run("gh", "release", "upload", tag, *paths)
    verify_remote(tag, expected, server_tag)


def write_manifest(directory, packages):
    path = directory / "SHA256SUMS.txt"
    path.write_text("".join(f"{packages[name][1].split(':', 1)[1]}  {name}\n"
                            for name in sorted(packages)), encoding="utf-8", newline="\n")
    return path.name


def create_draft(tag, server_tag, directory):
    reuse = tag != server_tag
    names = CLIENT + (SERVER if reuse else DESKTOP)
    if reuse:
        previous = source_assets(server_tag)
        run("gh", "release", "download", server_tag, "--dir", str(directory),
            *(arg for name in SERVER for arg in ("--pattern", name)))
    packages = {name: checksum(directory / name) for name in names}
    if reuse and any(packages[name] != metadata_checksum(previous[name]) for name in SERVER):
        raise ValueError("A reused server package does not match its original release.")

    # Write the extra information only for reused packages. Their internal guides keep the original tag.
    notes = Path("installer/ReleaseNotes.md").read_text(encoding="utf-8-sig")
    if reuse:
        notes += f"\n\nСерверные пакеты и приложение iPhone взяты из [Castar {server_tag[1:]}](https://github.com/krislechy/Castar/releases/tag/{server_tag}).\n"
        manifest = write_manifest(directory, packages)
        packages[manifest] = checksum(directory / manifest)
    with tempfile.TemporaryDirectory() as temp:
        path = Path(temp) / "notes.md"
        path.write_text(notes, encoding="utf-8")
        query = subprocess.run(("gh", "release", "view", tag, "--json", "tagName"),
                               capture_output=True, text=True, encoding="utf-8")
        if query.returncode:
            if "release not found" not in query.stderr.lower():
                raise ValueError(query.stderr.strip())
            run("gh", "release", "create", tag, "--verify-tag", "--draft", "--title", f"Castar {tag[1:]}",
                "--notes-file", str(path))
    upload_missing(tag, directory, packages, server_tag)
    if reuse and release(tag)["isDraft"]:
        run("gh", "release", "edit", tag, "--draft=false", "--latest")


def publish_full(tag, directory):
    if project_version("Castar.Server/Castar.Server.csproj") != tag:
        raise ValueError("Run this command from the tagged full-release source.")
    run("git", "diff", "--quiet", tag, "--", "Castar.Server.iOS", "Castar.Server/Castar.Server.csproj")
    with tarfile.open(directory / IPHONE[0], "r:") as server:
        actual = server.extractfile("./version").read().decode().strip()
        plist = server.extractfile("./CastarControl.app/Info.plist").read()
        guide = server.extractfile("./DEPLOYMENT.md").read()
    with zipfile.ZipFile(directory / IPHONE[1]) as app:
        if app.read("CastarControl.app/Info.plist") != plist or app.read("DEPLOYMENT.md") != guide:
            raise ValueError("The iPhone TAR and ZIP use different build metadata or guides.")
    info = plistlib.loads(plist)
    if f"v{actual}" != tag or info["CFBundleShortVersionString"] != actual:
        raise ValueError("The iPhone package does not match the full release version.")
    if plist != Path("Castar.Server.iOS/App/Info.plist").read_bytes() or b"/releases/tag/" + tag.encode() not in guide:
        raise ValueError("The iPhone app metadata or guide does not match the tagged release.")

    existing = verified_assets(release(tag), CLIENT + DESKTOP)
    packages = {name: metadata_checksum(entry) for name, entry in existing.items()}
    packages.update({name: checksum(directory / name) for name in IPHONE})
    manifest = write_manifest(directory, packages)
    packages[manifest] = checksum(directory / manifest)
    upload_missing(tag, directory, {name: packages[name] for name in (*IPHONE, manifest)}, tag)
    verify_remote(tag, packages, tag)
    if release(tag)["isDraft"]:
        run("gh", "release", "edit", tag, "--draft=false", "--latest")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)
    for command in ("plan", "draft", "publish-full"):
        args = sub.add_parser(command)
        args.add_argument("tag")
        if command == "plan":
            args.add_argument("output", type=Path)
        else:
            args.add_argument("directory", type=Path)
            if command == "draft":
                args.add_argument("server_tag")
    options = parser.parse_args()
    version(options.tag)
    if options.command == "plan":
        plan(options.tag, options.output)
    elif options.command == "draft":
        create_draft(options.tag, options.server_tag, options.directory)
    else:
        publish_full(options.tag, options.directory)
