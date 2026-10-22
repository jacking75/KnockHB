using RemoteAssist.Host.Core.Security;

internal static class InitialAdminRegression
{
    public static Task Run()
    {
        var clock=new Clock();
        var binding=new PermissionSnapshot("session","supporter",1,Capability.Screen|Capability.Control|Capability.Admin,clock.GetUtcNow().AddHours(2));
        var authorization=new AdminAuthorization(binding,clock);
        var approval=authorization.AuthorizeSessionRequest(Guid.NewGuid().ToString("N"));
        var configuration=new AdminConfiguration(approval,1,"user",new("display",1,0,0,1920,1080),"mapping",30,1920,1080,InitialSupport:true);
        configuration.ValidateLifetime(clock.Now);
        Reject(()=>(configuration with {InitialSupport=false}).ValidateLifetime(clock.Now));
        Reject(()=>(configuration with {Approval=approval with {ExpiresAt=clock.Now.AddHours(2).AddTicks(1)}}).ValidateLifetime(clock.Now));
        Reject(()=>(configuration with {Approval=approval with {ExpiresAt=clock.Now}}).ValidateLifetime(clock.Now));
        Reject(()=>(configuration with {Approval=approval with {ExpiresAt=clock.Now.AddSeconds(-1)}}).ValidateLifetime(clock.Now));
        (configuration with {InitialSupport=false,Approval=approval with {ExpiresAt=clock.Now.AddMinutes(2)}}).ValidateLifetime(clock.Now);
        if(approval.SessionId!=binding.SessionId||approval.ConnectionId!=binding.ConnectionId||approval.PermissionGeneration!=1||approval.ExpiresAt!=binding.ExpiresAt)
            throw new Exception("Initial administrator grant lost session binding or lifetime.");
        clock.Now=clock.Now.AddMinutes(3);
        if(!authorization.Allows(approval.AdminGeneration)||authorization.ProcessGrant(approval).ExpiresAt!=binding.ExpiresAt)
            throw new Exception("Initial administrator grant incorrectly requires another two-minute approval.");
        Reject(()=>authorization.AuthorizeSessionRequest(Guid.NewGuid().ToString("N")));
        Reject(()=>authorization.ProcessGrant(approval with {ConnectionId="other"}));
        authorization.End();Reject(()=>authorization.ProcessGrant(approval));
        Reject(()=>authorization.AuthorizeSessionRequest(Guid.NewGuid().ToString("N")));
        var expired=new AdminAuthorization(binding,clock);var expires=expired.AuthorizeSessionRequest(Guid.NewGuid().ToString("N"));
        clock.Now=binding.ExpiresAt;
        if(expired.Allows(expires.AdminGeneration))throw new Exception("Expired initial grant remains active.");
        Reject(()=>expired.ProcessGrant(expires));
        var denied=new AdminAuthorization(binding with {ExpiresAt=clock.Now.AddHours(1),Capabilities=Capability.Screen|Capability.Control},clock);
        Reject(()=>denied.AuthorizeSessionRequest(Guid.NewGuid().ToString("N")));
        var pending=new AdminAuthorization(binding with {ExpiresAt=clock.Now.AddHours(1)},clock);
        pending.Request(Guid.NewGuid().ToString("N"));Reject(()=>pending.AuthorizeSessionRequest(Guid.NewGuid().ToString("N")));
        return Task.CompletedTask;
    }
    static void Reject(Action action)
    {
        try{action();}catch(Exception ex)when(ex is UnauthorizedAccessException or InvalidOperationException){return;}
        throw new Exception("Invalid initial administrator authorization accepted.");
    }
    sealed class Clock:TimeProvider
    {
        public DateTimeOffset Now=new(2026,10,22,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>Now;
    }
}
