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
        BlueprintConfig.Bind(this);
        BuildCameraConfig.Bind(this);
        PlacementControlConfig.Bind(this);
        DvergrCircletConfig.Bind(this);
    }

}
