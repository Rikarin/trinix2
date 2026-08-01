using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Trinix.Daemon;

// trinixd exists to answer one question: can a C# process be a well-behaved
// systemd service on this distribution? "Well-behaved" is a specific list — it
// must notify readiness rather than be assumed ready, log to the journal with
// real severities rather than as undifferentiated stdout, and stop when asked
// instead of when killed.
//
// AddSystemd() supplies all three: it switches the host lifetime to systemd's
// notify protocol and the console logger to the journal's severity prefixes.
// Everything Trinix later ships as a service inherits this shape, which is the
// entire point of standing it up now rather than alongside the compositor.

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSystemd();
builder.Services.AddHostedService<SystemStatusService>();

await builder.Build().RunAsync();
