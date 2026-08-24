using RemoteAssist.Host.Service;

var builder = Host.CreateApplicationBuilder(args);

// 서비스로 등록해 실행할 때도, 콘솔에서 디버깅할 때도 동작한다.
builder.Services.AddWindowsService(o => o.ServiceName = "RemoteAssist Host");
builder.Services.AddHostedService<HostAgentWorker>();

var host = builder.Build();
host.Run();
