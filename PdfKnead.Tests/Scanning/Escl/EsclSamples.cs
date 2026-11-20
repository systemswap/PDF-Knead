namespace PdfKnead.Tests.Scanning.Escl;

/// <summary>Answers of an invented eSCL scanner for the tests — composed for these tests, values as a typical office device might report.</summary>
internal static class EsclSamples
{
    public const string Scan = "http://schemas.hp.com/imaging/escl/2011/05/03";
    public const string Pwg = "http://www.pwg.org/schemas/2010/12/sm";

    public const string Capabilities = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <scan:ScannerCapabilities xmlns:scan="{Scan}" xmlns:pwg="{Pwg}">
          <pwg:Version>2.63</pwg:Version>
          <pwg:MakeAndModel>Beispiel Büro 1000</pwg:MakeAndModel>
          <pwg:SerialNumber>TEST0001</pwg:SerialNumber>
          <scan:UUID>{"{"}11111111-2222-3333-4444-555555555555{"}"}</scan:UUID>
          <scan:SettingProfiles>
            <scan:SettingProfile name="einzug">
              <scan:ColorModes>
                <scan:ColorMode>RGB24</scan:ColorMode>
                <scan:ColorMode>Grayscale8</scan:ColorMode>
              </scan:ColorModes>
              <scan:DocumentFormats>
                <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
                <scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt>
              </scan:DocumentFormats>
              <scan:SupportedResolutions>
                <scan:DiscreteResolutions>
                  <scan:DiscreteResolution><scan:XResolution>150</scan:XResolution><scan:YResolution>150</scan:YResolution></scan:DiscreteResolution>
                  <scan:DiscreteResolution><scan:XResolution>300</scan:XResolution><scan:YResolution>300</scan:YResolution></scan:DiscreteResolution>
                  <scan:DiscreteResolution><scan:XResolution>200</scan:XResolution><scan:YResolution>200</scan:YResolution></scan:DiscreteResolution>
                </scan:DiscreteResolutions>
              </scan:SupportedResolutions>
            </scan:SettingProfile>
          </scan:SettingProfiles>
          <scan:Platen>
            <scan:PlatenInputCaps>
              <scan:MinWidth>16</scan:MinWidth>
              <scan:MaxWidth>2550</scan:MaxWidth>
              <scan:MinHeight>16</scan:MinHeight>
              <scan:MaxHeight>3508</scan:MaxHeight>
              <scan:SettingProfiles>
                <scan:SettingProfile>
                  <scan:ColorModes>
                    <scan:ColorMode>BlackAndWhite1</scan:ColorMode>
                    <scan:ColorMode>Grayscale8</scan:ColorMode>
                    <scan:ColorMode>RGB24</scan:ColorMode>
                  </scan:ColorModes>
                  <scan:DocumentFormats>
                    <pwg:DocumentFormat>application/pdf</pwg:DocumentFormat>
                    <pwg:DocumentFormat>image/jpeg</pwg:DocumentFormat>
                    <scan:DocumentFormatExt>application/pdf</scan:DocumentFormatExt>
                    <scan:DocumentFormatExt>image/jpeg</scan:DocumentFormatExt>
                  </scan:DocumentFormats>
                  <scan:SupportedResolutions>
                    <scan:ResolutionRange>
                      <scan:XResolutionRange><scan:Min>75</scan:Min><scan:Max>1200</scan:Max><scan:Normal>300</scan:Normal><scan:Step>25</scan:Step></scan:XResolutionRange>
                      <scan:YResolutionRange><scan:Min>75</scan:Min><scan:Max>1200</scan:Max><scan:Normal>300</scan:Normal><scan:Step>25</scan:Step></scan:YResolutionRange>
                    </scan:ResolutionRange>
                  </scan:SupportedResolutions>
                </scan:SettingProfile>
              </scan:SettingProfiles>
            </scan:PlatenInputCaps>
          </scan:Platen>
          <scan:Adf>
            <scan:AdfSimplexInputCaps>
              <scan:MinWidth>300</scan:MinWidth>
              <scan:MaxWidth>2550</scan:MaxWidth>
              <scan:MinHeight>300</scan:MinHeight>
              <scan:MaxHeight>4200</scan:MaxHeight>
              <scan:SettingProfiles><scan:SettingProfile ref="einzug"/></scan:SettingProfiles>
            </scan:AdfSimplexInputCaps>
            <scan:AdfDuplexInputCaps>
              <scan:MinWidth>300</scan:MinWidth>
              <scan:MaxWidth>2550</scan:MaxWidth>
              <scan:MinHeight>300</scan:MinHeight>
              <scan:MaxHeight>3600</scan:MaxHeight>
              <scan:SettingProfiles><scan:SettingProfile ref="einzug"/></scan:SettingProfiles>
            </scan:AdfDuplexInputCaps>
            <scan:FeederCapacity>50</scan:FeederCapacity>
            <scan:AdfOptions>
              <scan:AdfOption>DetectPaperLoaded</scan:AdfOption>
              <scan:AdfOption>Duplex</scan:AdfOption>
            </scan:AdfOptions>
          </scan:Adf>
        </scan:ScannerCapabilities>
        """;

    public const string StatusIdle = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <scan:ScannerStatus xmlns:scan="{Scan}" xmlns:pwg="{Pwg}">
          <pwg:Version>2.63</pwg:Version>
          <pwg:State>Idle</pwg:State>
          <scan:AdfState>ScannerAdfLoaded</scan:AdfState>
        </scan:ScannerStatus>
        """;

    /// <summary>A state with two jobs: the current one scanning, an older one cancelled at the device.</summary>
    public const string StatusWithJobs = $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <scan:ScannerStatus xmlns:scan="{Scan}" xmlns:pwg="{Pwg}">
          <pwg:Version>2.63</pwg:Version>
          <pwg:State>Processing</pwg:State>
          <scan:AdfState>ScannerAdfProcessing</scan:AdfState>
          <scan:Jobs>
            <scan:JobInfo>
              <pwg:JobUri>/eSCL/ScanJobs/aaaa0000-0000-4000-8000-000000000001</pwg:JobUri>
              <pwg:JobUuid>aaaa0000-0000-4000-8000-000000000001</pwg:JobUuid>
              <scan:Age>3</scan:Age>
              <pwg:ImagesCompleted>2</pwg:ImagesCompleted>
              <pwg:ImagesToTransfer>1</pwg:ImagesToTransfer>
              <pwg:JobState>Processing</pwg:JobState>
              <pwg:JobStateReasons><pwg:JobStateReason>JobScanning</pwg:JobStateReason></pwg:JobStateReasons>
            </scan:JobInfo>
            <scan:JobInfo>
              <pwg:JobUri>/ScanJobs/bbbb0000-0000-4000-8000-000000000002</pwg:JobUri>
              <scan:Age>400</scan:Age>
              <pwg:ImagesCompleted>0</pwg:ImagesCompleted>
              <pwg:JobState>Canceled</pwg:JobState>
              <pwg:JobStateReasons><pwg:JobStateReason>JobCanceledByUser</pwg:JobStateReason></pwg:JobStateReasons>
            </scan:JobInfo>
          </scan:Jobs>
        </scan:ScannerStatus>
        """;
}
