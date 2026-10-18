using System.ServiceProcess;
// Deliberately has no screen, input, networking or child processes.
public sealed class ScmFixture : ServiceBase {
 public ScmFixture(string name) { ServiceName=name; CanStop=true; }
 protected override void OnStart(string[] args) { }
 protected override void OnStop() { }
 public static void Main(string[] args) { ServiceBase.Run(new ScmFixture(args[0])); }
}
