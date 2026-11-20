using PdfKnead.Ai;
using Xunit;

namespace PdfKnead.Tests.Ocr;

/// <summary>ONNX Runtime starts without its telemetry (docs/FEHLERGEDAECHTNIS.md, No. 292).</summary>
public class OnnxRuntimeSetupTests
{
    [Fact]
    public void TelemetrySwitch_VisibleToNativeCode()
    {
        OnnxRuntimeSetup.Prepare();
        Assert.Equal("1", OnnxRuntimeSetup.NativeValue(OnnxRuntimeSetup.TelemetryVariable));
    }

    [Fact]
    public void VariableSetFromDotNet_NativeCodeSeesIt()
    {
        // what .NET alone sets does not reach getenv on Linux: that is why the setup sets it there as well
        Environment.SetEnvironmentVariable("PDFKNEAD_TEST_ONLY_DOTNET", "ja");
        if (!OperatingSystem.IsWindows()) Assert.Null(OnnxRuntimeSetup.NativeValue("PDFKNEAD_TEST_ONLY_DOTNET"));
        const string name = "PDFKNEAD_TEST_NATIVE_ENV";
        OnnxRuntimeSetup.SetForNativeCode(name, "ja");
        Assert.Equal("ja", OnnxRuntimeSetup.NativeValue(name));
    }
}
