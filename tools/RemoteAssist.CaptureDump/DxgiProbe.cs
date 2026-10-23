// 하이브리드 GPU 진단: 어댑터·출력 배치와 출력별 Desktop Duplication 결과(HRESULT)만 출력한다.
// 픽셀은 읽거나 저장하지 않는다.
//   dotnet run --project tools/RemoteAssist.CaptureDump -- --probe-dxgi

using System.Diagnostics;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

internal static class DxgiProbe
{
    public static int Run()
    {
        Console.WriteLine($"user={Environment.UserName} session={Process.GetCurrentProcess().SessionId} os={Environment.OSVersion.Version}");
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out var adapter).Success; a++)
        {
            using (adapter)
            {
                var desc = adapter.Description1;
                Console.WriteLine($"adapter[{a}] {desc.Description} luid={desc.Luid.HighPart:X8}:{desc.Luid.LowPart:X8} vendor=0x{desc.VendorId:X4} flags={desc.Flags}");
                for (uint o = 0; adapter.EnumOutputs(o, out var output).Success; o++)
                {
                    using (output)
                    {
                        var od = output.Description;
                        Console.WriteLine($"  output[{o}] {od.DeviceName} attached={od.AttachedToDesktop} rect={od.DesktopCoordinates}");
                        Console.WriteLine($"    DuplicateOutput: {Try(adapter, output, false)}");
                        Console.WriteLine($"    DuplicateOutput1: {Try(adapter, output, true)}");
                    }
                }
            }
        }
        using var factory6 = factory.QueryInterfaceOrNull<IDXGIFactory6>();
        if (factory6 is not null)
            foreach (var preference in new[] { GpuPreference.MinimumPower, GpuPreference.HighPerformance })
            {
                factory6.EnumAdapterByGpuPreference(0, preference, out IDXGIAdapter1? first).CheckError();
                using (first) Console.WriteLine($"first adapter for {preference}: {first!.Description1.Description}");
            }
        return 0;
    }

    static string Try(IDXGIAdapter1 adapter, IDXGIOutput output, bool output5)
    {
        try
        {
            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                out ID3D11Device? device, out ID3D11DeviceContext? context).CheckError();
            using (device) using (context)
            {
                if (!output5)
                {
                    using var o1 = output.QueryInterface<IDXGIOutput1>();
                    using var dup = o1.DuplicateOutput(device);
                    return "OK " + dup.Description.ModeDescription.Width + "x" + dup.Description.ModeDescription.Height;
                }
                using var o5 = output.QueryInterfaceOrNull<IDXGIOutput5>();
                if (o5 is null) return "IDXGIOutput5 없음";
                using var dup5 = o5.DuplicateOutput1(device, [Format.B8G8R8A8_UNorm]);
                return "OK " + dup5.Description.ModeDescription.Width + "x" + dup5.Description.ModeDescription.Height;
            }
        }
        catch (SharpGenException ex) { return $"FAIL 0x{ex.HResult:X8}"; }
    }
}
