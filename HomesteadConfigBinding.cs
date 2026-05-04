namespace Homestead;

public partial class HomesteadPlugin
{
    private void BindConfiguration()
    {
        GeneralConfig.Bind(this);
        ClientConfig.Bind(this);
        BlueprintConfig.Bind(this);
        BuildCameraConfig.Bind(this);
        PlacementControlConfig.Bind(this);
        DvergrCircletConfig.Bind(this);
        ZoneBundleConfig.Bind(this);
        AutoArchiveConfig.Bind(this);
    }
}
