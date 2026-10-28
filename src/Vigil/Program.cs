using System.CommandLine;
using Vigil;
using Vigil.Commands;

var root = CommandFactory.Create(VigilPaths.ForCurrentUser());
return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = null });
