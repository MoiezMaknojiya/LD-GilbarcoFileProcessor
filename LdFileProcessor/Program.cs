using ApiLibrary;
using ApiLibrary.Utilities;
using LdFileProcessor;
using Serilog;
using Serilog.Events;



// Configure Serilog before building the host
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("System", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        path: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "LdPosService",
            "logs",
            "service-.log"
        ),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

try
{
    Log.Information("Starting LdFileProcessor Windows Service");

    var builder = Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices(services =>
        {
            services.AddSingleton<XmlJsonConverter>();
            services.AddSingleton<FileUtilities>();
            services.AddSingleton<ApiServices>();
            services.AddHostedService<FileMonitorService>();
        })
        .UseWindowsService(options =>
        {
            options.ServiceName = "LdFileProcessor";
        });

    var host = builder.Build();
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}


//var builder = Host.CreateApplicationBuilder(args);
//builder.Services.AddHostedService<FileMonitorService>();

//var host = builder.Build();
//host.Run();
