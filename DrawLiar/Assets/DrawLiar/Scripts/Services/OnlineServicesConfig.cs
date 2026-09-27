using UnityEngine;

namespace DrawLiar
{
    [CreateAssetMenu(menuName = "DrawLiar/Online Services")]
    public sealed class OnlineServicesConfig : ScriptableObject
    {
        public string EnvironmentName = "production";
        public uint SteamAppId;
    }
}
