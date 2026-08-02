using Build.Modules;
using Build.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModularPipelines;
using ModularPipelines.Extensions;

var builder = Pipeline.CreateBuilder();

builder.Configuration.AddJsonFile("appsettings.json");
builder.Configuration.AddUserSecrets<Program>();
builder.Configuration.AddEnvironmentVariables();

builder.Services.AddOptions<BuildOptions>().Bind(builder.Configuration.GetSection("Build"));

if (args.Length == 0)
{
    // No arguments = compile only.
    builder.Services.AddModule<CompileProjectModule>();
}
else if (args.Contains("pack"))
{
    builder.Services.AddModule<CleanProjectModule>();
    builder.Services.AddModule<CreateInstallerModule>();
}
else
{
    // Anything else registered ZERO modules, ran nothing, and exited 0 — a "successful" no-op. In a build
    // system whose docs warn twice that an exit-0 can mask a dropped config/MSI, a typo ("pak", "--pack",
    // "Pack" — the match is case-sensitive) must not look like a green build.
    throw new ArgumentException(
        $"Unknown argument(s): {string.Join(' ', args)}. " +
        "Usage: `dotnet run` (compile only) or `dotnet run -- pack` (clean + build the installer).");
}

await builder.Build().RunAsync();