using DesktopTestDriver;

try
{
    Wire.ConfigureConsole();
    if (args is ["desktop-plan", var planRole, var planOutput])
    {
        var plan = DesktopFixturePlan.Create(planRole); plan.Validate();
        using var file = new FileStream(planOutput, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(System.Text.Encoding.UTF8.GetBytes(Wire.Encode(plan)));
        Console.WriteLine(Wire.Encode(new { plan.Id, planHash = plan.Hash })); return 0;
    }
    if (args is ["desktop-run", var inventoryPath, var machineId, var role, var configFile, var python, var sshAdapter, var packagePath, var reservationRoot, var bundle])
    {
        var inventory = Wire.Decode<Inventory>(File.ReadAllText(inventoryPath)); var machine = inventory.Resolve(machineId, role);
        var config = Wire.Decode<RunConfig>(File.ReadAllText(configFile)); config.Validate(); Authorization.Verify(config, requireCurrentAccount: false);
        var plan = DesktopFixturePlan.Create(role);
        var context = new ScenarioContext(config.RunId, config.Fixture?.ImageHash ?? throw new DriverException(Error.DelegationMismatch),
            Wire.FileHash(Environment.ProcessPath!), Wire.FileHash(configFile), [config], false);
        context.Validate(plan);
        if (config.MachineId != machine.Id || config.Role != role || config.Delegation!.Owner != machine.ConsoleUser ||
            config.RecoveryEvidence != machine.Recovery.EvidencePath || !config.Fixture!.ImagePath.Equals(
                Path.GetFullPath(Path.Combine(machine.InstallRoot,config.RunId,"package","MarkerWindow","MarkerWindow.exe")),StringComparison.OrdinalIgnoreCase)) throw new DriverException(Error.DelegationMismatch);
        if (Directory.Exists(bundle) || File.Exists(bundle)) throw new DriverException(Error.ReservationConflict);
        RunStore.CheckPrivate(Path.GetDirectoryName(Path.GetFullPath(bundle))!);
        var proof = Wire.Decode<RecoveryReceipt>(File.ReadAllText(machine.Recovery.EvidencePath ?? throw new DriverException(Error.RecoveryRequired)));
        RecoveryGate.Require(RecoveryGrade.R1, machine, proof);
        if (proof.RunId != config.RunId) throw new DriverException(Error.RecoveryRequired);
        var reservations = new Reservations(reservationRoot);
        reservations.Acquire(inventory, config.RunId, [new(machine.Id, role)], context.CandidateHash, context.SettingsHash, config.ExpiresAt);
        var node = new SshTransport(python, sshAdapter, machine, config.RunId);
        // Deploy and immediately start the lease in this process; a manual CLI handoff cannot meet a 3s lease.
        using (var installation = new CancellationTokenSource(125000))
            await node.Invoke<System.Text.Json.JsonElement>("install", new { packagePath = Path.GetFullPath(packagePath), configPath = Path.GetFullPath(configFile) }, installation.Token);
        var ui = new NodeUi(node, config, context.SettingsHash);
        var began = System.Diagnostics.Stopwatch.GetTimestamp();
        using var overall = new CancellationTokenSource(TimeSpan.FromSeconds(plan.MaximumSeconds - 15));
        await using var lease = ui.BeginLease();
        bool clean = false;
        try
        {
            async Task<bool> Ready(CancellationToken cancellation)
            {
                var status = await node.Invoke<ClientStatus>("status", null, cancellation);
                var inspected = Wire.Decode<InspectResult>(Wire.Encode((await ui.Send("inspect", cancellation)).Evidence));
                return lease.Healthy && status.ManifestHash == context.SettingsHash && status.Session == config.TargetSession && status.Desktop == "Default" &&
                    inspected.Observation.Session == config.TargetSession && inspected.Observation.Desktop == "Default" && !inspected.Observation.Elevated && inspected.Observation.Monitors.Length != 0;
            }
            var registry = new AdapterRegistry(); registry.Register(new DesktopFixtureAdapter(ui, config, Ready));
            var report = await new ScenarioRunner(registry).Run(plan, context, overall.Token);
            using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(.01, plan.MaximumSeconds - System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds)));
            clean = await node.Stop(finish.Token);
            report = report with { CleanupVerified = report.CleanupVerified && clean, Verdict = clean ? report.Verdict : Verdict.Fail };
            EvidenceBundle.Write(bundle, plan, report);
            var collected = Path.Combine(Path.GetFullPath(bundle), "client-evidence"); RunStore.CreatePrivateDirectory(collected);
            try
            {
                await node.Invoke<System.Text.Json.JsonElement>("collect", new { outputDirectory = collected }, finish.Token);
                EvidenceBundle.Complete(bundle, plan, report with { DurationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds });
                if (clean) reservations.Complete(machine.Id, config.RunId, true);
            }
            catch
            {
                EvidenceBundle.Complete(bundle, plan, report with { Verdict = Verdict.Fail, CleanupVerified = false,
                    DurationMilliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalMilliseconds });
                throw;
            }
            Console.WriteLine(File.ReadAllText(Path.Combine(bundle, "public-summary.json"))); return report.Verdict == Verdict.Pass ? 0 : 1;
        }
        finally
        {
            if (!clean)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Min(3,Math.Max(.01,plan.MaximumSeconds-System.Diagnostics.Stopwatch.GetElapsedTime(began).TotalSeconds))));
                try { await node.Stop(cleanup.Token); } catch { /* Reservation remains dirty; never silently reassign it. */ }
            }
        }
    }
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
    Console.Error.WriteLine("desktop-plan ROLE OUTPUT | desktop-run INVENTORY MACHINE ROLE CONFIG PYTHON SSH_ADAPTER PACKAGE RESERVATIONS BUNDLE | owner-init DIRECTORY KEY_ID | authorize CONFIG PRIVATE_KEY OUTPUT | plan-check PLAN | catalog-check CATALOG | bundle-check BUNDLE | purge-evidence STATE | simulate PLAN CONFIG OUTPUT | validate INVENTORY | reserve INVENTORY RESERVATIONS RUN ASSIGNMENTS CANDIDATE_HASH SETTINGS_HASH EXPIRY | ssh INVENTORY MACHINE ROLE RUN PYTHON ADAPTER OPERATION");
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine(Wire.Encode(new { errorCode = e is DriverException d ? d.Code : Error.InvalidRequest }));
    return 1;
}
