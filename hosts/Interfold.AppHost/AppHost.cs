using Interfold.AppHost;

var builder = DistributedApplication.CreateBuilder(args);
InterfoldAppHost.Configure(builder);
builder.Build().Run();
