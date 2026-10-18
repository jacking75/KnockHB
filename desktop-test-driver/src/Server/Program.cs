using DesktopTestDriver;

try
{
    Wire.ConfigureConsole();
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
    Console.Error.WriteLine("validate INVENTORY | reserve INVENTORY RESERVATIONS RUN ASSIGNMENTS CANDIDATE_HASH SETTINGS_HASH EXPIRY | ssh INVENTORY MACHINE ROLE RUN PYTHON ADAPTER OPERATION");
    return 2;
}
catch (Exception e)
{
    Console.Error.WriteLine(Wire.Encode(new { errorCode = e is DriverException d ? d.Code : Error.InvalidRequest }));
    return 1;
}
