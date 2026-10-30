using Vigil;

// The windowless daemon (see Vigild.csproj): same code as `vigil run`, started by the Run key `vigil install` writes.
// There is no console to press Ctrl+C in; `vigil uninstall` stops it over IPC.
return await VigilApplication.RunDaemonAsync(args, CancellationToken.None);
