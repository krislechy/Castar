using Castar.Server;

if (args is ["--supervise-server", var installationRoot])
    return await DesktopServerSupervisor.RunAsync(installationRoot);
if (args is ["--import-legacy", .. var importArguments])
    return await LegacyImport.RunAsync(importArguments);

return await ServerHost.RunAsync(args);
