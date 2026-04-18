using CitizenFX.Core;

using FRFuel.Shared.Models;

using System;

namespace FRFuel.Server
{
    public class Server : BaseScript
    {
        #region Event Handlers

        [EventHandler("playerDropped")]
        internal void OnPlayerDropped([FromSource] Player plyr, string reason, string resourceName, uint clientDropReason) => TriggerClientEvent("frfuel:removeAccessories", int.Parse(plyr.Handle));

        [EventHandler("frfuel:server:createAccessories")]
        internal void OnCreateAccessories([FromSource] Player plyr, Vector3 position) => TriggerClientEvent("frfuel:createAccessories", int.Parse(plyr.Handle), position);

        [EventHandler("frfuel:server:removeAccessories")]
        internal void OnRemoveAccessories([FromSource] Player plyr) => TriggerClientEvent("frfuel:removeAccessories", int.Parse(plyr.Handle));

        [EventHandler("frfuel:server:removeAllAccessories")]
        internal void OnRemoveAllAccessories([FromSource] Player plyr)
        {
            foreach (FuelAccessories fuelAccessories in FuelAccessories.GetAllAccessories())
            {
                if (fuelAccessories == null)
                {
                    continue;
                }

                TriggerClientEvent("frfuel:removeAccessories", fuelAccessories.PlayerId);
            }
        }

        #endregion
    }
}
