namespace TeraDeepOcean
{
    public partial class Plugin : IAssemblyPlugin
    {
        // Client-specific code
        partial void InitializeClient()
        {
            LuaCsSetup.Instance.Hook.Add(
                "think",
                "TeraDeepOcean.ConfigMenuUpdate",
                (object[] args) =>
                {
                    double deltaTime = Convert.ToDouble(args[0]);
                    TLVideoPlayer.Update();
                    TLConfigMenu.Update((float)deltaTime);
                    return null;
                }
            );
        }
    }
}
