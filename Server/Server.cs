using CitizenFX.Core;

namespace FRFuel.Server
{
    public class Server : BaseScript
    {
        #region Event Handlers

        [EventHandler("frfuel:server:createAccessories")]
        internal void OnCreateAccessories([FromSource] Player plyr, Vector3 position) => TriggerClientEvent("frfuel:createAccessories", int.Parse(plyr.Handle), position);

        [EventHandler("frfuel:server:removeAccessories")]
        internal void OnRemoveAccessories([FromSource] Player plyr) => TriggerClientEvent("frfuel:removeAccessories", int.Parse(plyr.Handle));

        #endregion
    }
}
