using DesktopTestDriver;

try
{
    Wire.ConfigureConsole();
    if (args is ["owner-init", var directory, var keyId])
    {
        var owner = OwnerProvisioning.Initialize(directory,keyId);
        Console.WriteLine(Wire.Encode(new { owner.Owner, owner.KeyId, publicKeyHash = Wire.Hash(Convert.FromBase64String(owner.PublicKey)) })); return 0;
    }
    if (args is ["authorize", var configPath, var keyPath, var output])
    {
        OwnerProvisioning.Authorize(configPath,keyPath,output);
        Console.WriteLine(Wire.Encode(new { authorized = true, manifestHash = Wire.FileHash(output) })); return 0;
    }
    if (args is ["plan-check", var planPath])
    {
        var plan = Wire.Decode<ScenarioPlan>(File.ReadAllText(planPath)); plan.Validate();
        Console.WriteLine(Wire.Encode(new { valid = true, plan.Id, planHash = plan.Hash })); return 0;
    }
    if (args is ["catalog-check", var catalogPath])
    {
        var catalog = Wire.Decode<AcceptanceCase[]>(File.ReadAllText(catalogPath)); AcceptanceGate.Validate(catalog);
        Console.WriteLine(Wire.Encode(new { valid = true, cases = catalog.Length, productAcceptance = "NOT RUN" })); return 0;
    }
    if (args is ["bundle-check", var bundlePath])
    {
        EvidenceBundle.Verify(bundlePath); Console.WriteLine(Wire.Encode(new { valid = true })); return 0;
    }
    if (args is ["purge-evidence", var statePath])
    {
        var removed = EvidenceVault.PurgeExpired(new RunStore(statePath),DateTimeOffset.UtcNow);
        Console.WriteLine(Wire.Encode(new { removed })); return 0;
    }
    if (args is ["simulate", var samplePath, var sampleConfig, var sampleOutput])
    {
        var plan = Wire.Decode<ScenarioPlan>(File.ReadAllText(samplePath));
        var config = Wire.Decode<RunConfig>(File.ReadAllText(sampleConfig));
        var registry = new AdapterRegistry(); registry.Register(new CounterAdapter());
        var context = new ScenarioContext(config.RunId,Wire.FileHash(samplePath),Wire.FileHash(Environment.ProcessPath!),Wire.FileHash(sampleConfig),[config],true);
        var report = await new ScenarioRunner(registry).Run(plan,context); EvidenceBundle.Write(sampleOutput,plan,report);
        Console.WriteLine(File.ReadAllText(Path.Combine(sampleOutput,"public-summary.json"))); return report.Verdict == Verdict.Pass ? 0 : 1;
    }
    if (args.Length == 2 && args[0] == "validate")
    {
        var inventory = Wire.Decode<Inventory>(File.ReadAllText(args[1])); inventory.Validate();
        Console.WriteLine(Wire.Encode(new { valid = true, machines = inventory.Machines.Length }));
        return 0;
    }
    if (args.Length == 8 && args[0] == "reserve")
    {
        var inventory = Wire.Decode<Inventory>(File.ReadAllText(args[1]));
        var assignments = Wire.Decode<Assignment[]>(File.ReadAllText(args[4]));
        new Reservations(args[2]).Acquire(inventory, args[3], assignments, args[5], args[6], DateTimeOffset.Parse(args[7], System.Globalization.CultureInfo.InvariantCulture));
        Console.WriteLine(Wire.Encode(new { reserved = true, runId = args[3] })); return 0;
    }
    if (args.Length == 8 && args[0] == "ssh")
    {
        var inventory = Wire.Decode<Inventory>(File.ReadAllText(args[1]));
        var machine = inventory.Resolve(args[2], args[3]);
        var transport = new SshTransport(args[5], args[6], machine, args[4]);
        using var timeout = new CancellationTokenSource(125000);
        var payload = await Lines.Read(Console.In, timeout.Token);
        var data = payload == null ? (object?)null : Wire.Decode<System.Text.Json.JsonElement>(payload);
        var response = await transport.Invoke<System.Text.Json.JsonElement>(args[7], data, timeout.Token);
        Console.WriteLine(Wire.Encode(response)); return 0;
    }
    Console.Error.WriteLine("owner-init DIRECTORY KEY_ID | authorize CONFIG PRIVATE_KEY OUTPUT | plan-check PLAN | catalog-check CATALOG | bundle-check BUNDLE | purge-evidence STATE | simulate PLAN CONFIG OUTPUT | validate INVENTORY | reserve INVENTORY RESERVATIONS RUN ASSIGNMENTS CANDIDATE_HASH SETTINGS_HASH EXPIRY | ssh INVENTORY MACHINE ROLE RUN PYTHON ADAPTER OPERATION");
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine(Wire.Encode(new { errorCode = e is DriverException d ? d.Code : Error.InvalidRequest }));
    return 1;
}
