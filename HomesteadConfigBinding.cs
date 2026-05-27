namespace Homestead;

public partial class HomesteadPlugin
{
    private void BindConfiguration()
    {
        BindSharedConfiguration();
        BindConstructionConfiguration();
    }

    private void BindSharedConfiguration()
    {
        GeneralConfig.Bind(this);
        ClientConfig.Bind(this);
    }

    private void BindConstructionConfiguration()
    {
        DvergrCircletConfig.Bind(this);
        PlacementControlConfig.Bind(this);
        BuildCameraConfig.Bind(this);
        BlueprintConfig.Bind(this);
    }

}
