using CitizenFX.Core;

using System.Collections.Generic;
using System.Linq;

namespace FRFuel.Shared.Models
{
    public class FuelAccessories
    {
        /// <summary>
        /// The handle of the rope
        /// </summary>
        public int HoseId { get; set; }
        /// <summary>
        /// The networkd Id of the nozzle
        /// </summary>
        public int NozzleId { get; set; }
        /// <summary>
        /// The handle of the owner with the rope
        /// </summary>
        public int PlayerId { get; set; }
        /// <summary>
        /// Position of the rope
        /// </summary>
        public Vector3 Position { get; set; }

        /// <summary>
        /// Key: Player ID
        /// Value: KVP: Hose Network ID, Hose/Pump Position
        /// </summary>
        internal static Dictionary<int, FuelAccessories> _fuelAccessories = new Dictionary<int, FuelAccessories>();

        /// <summary>
        /// initialize a new fuel accesory
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="data">KVP: hose netId, hose position (gas pump)</param>
        public FuelAccessories(int playerId, int nozzleId, KeyValuePair<int, Vector3> data)
        {
            HoseId = data.Key;
            NozzleId = nozzleId;
            PlayerId = playerId;
            Position = data.Value;
        }

        /// <summary>
        /// Register fuel accessories for a player & nozzle
        /// </summary>
        /// <param name="playerId"></param>
        /// <param name="nozzleId">Nozzle prop netId</param>
        /// <param name="data">KVP: hose netIz, hose pos</param>
        public static void Register(int playerId, int nozzleId, KeyValuePair<int, Vector3> data) => _fuelAccessories[playerId] = new FuelAccessories(playerId, nozzleId, data);

        /// <summary>
        /// unregister fuel accessories for a player
        /// </summary>
        /// <param name="playerId"></param>
        public static void Unregister(int playerId)
        {
            if (_fuelAccessories.ContainsKey(playerId))
            {
                _fuelAccessories.Remove(playerId);
            }
        }

        /// <summary>
        /// Get the fuel accessories by player Id
        /// </summary>
        /// <param name="playerId"></param>
        /// <returns>FuelAccessories</returns>
        public static FuelAccessories GetAccessoriesByPlayer(int playerId) => _fuelAccessories.TryGetValue(playerId, out FuelAccessories accessories) ? accessories : null;

        /// <summary>
        /// Get all fuel accessories
        /// </summary>
        /// <returns>List<FuelAccessories></returns>
        public static List<FuelAccessories> GetAllAccessories() => _fuelAccessories.Values.ToList();
    }
}
