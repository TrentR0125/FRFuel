#undef MANUAL_ENGINE_CUTOFF

using CitizenFX.Core;
using CitizenFX.Core.Native;
using CitizenFX.Core.UI;

using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;

using static CitizenFX.Core.Native.API;

namespace FRFuel
{
    public class FRFuel : BaseScript
    {
        delegate float GetCurrentFuelLevelDelegate();
        delegate void AddFuel(float amount);
        delegate void SetFuel(float amount);

        #region General Variables

        private int _currentGasStationIndex;

        protected bool _refuelAllowed = true;

        protected bool _showHud = true;
        protected bool _showHudWhenEngineOff = true;

        protected bool _initialized = false;
        protected bool _currentVehicleFuelLevelInitialized = false;

        protected Config Config { get; set; }
        protected Vehicle LastVehicle { get => _lastVehicle; set => _lastVehicle = value; }
        
        public HUD _hud;

        public Random _random = new Random();
        
        private Vehicle _lastVehicle;
        
        protected InLoopOutAnimation _jerryCanAnimation;
        
        protected Blip[] _blips;

        protected List<Pickup> _pickups;

        #endregion

        #region Constant Variables

        protected float FUEL_TANK_CAPACITY = 65f;

        protected float REFUEL_RATE = 1f;
        protected float ADDED_FUEL_CAPACITOR = 0f;
        protected float FUEL_CONSUMPTION_RATE = 1f;
        protected float FUEL_ACCELERATION_IMPACT = 0.0002f;
        protected float FUEL_TRACTION_IMPACT = 0.0001f;
        protected float FUEL_RPM_IMPACT = 0.0005f;

        public float SHOW_MARKER_RANGE = 250f;

        public static string FUEL_LEVEL_DECOR = "_Fuel_Level";
        public static string JERRYCAN_ANIM_DICT = "weapon@w_sp_jerrycan";

        private string NORMAL_HEX = "FFB300"; // 255, 179, 0
        private string WARNING_HEX = "FFF5DC"; // 255, 179, 0

        public static Control ENGINE_TOGGLE = Control.ThrowGrenade; // INPUT_THROW_GRENADE

        public static string[] TANK_BONES = new string[] {
            "petrolcap",
            "petroltank",
            "petroltank_r",
            "petroltank_l",
            "wheel_lr"
        };

        internal IReadOnlyList<Model> GAS_PUMP_MODELS = new List<Model>()
        {
            new Model("prop_gas_pump_1a"),
            new Model("prop_gas_pump_1b"),
            new Model("prop_gas_pump_1c"),
            new Model("prop_gas_pump_1d"),
            new Model("prop_gas_pump_old2"),
            new Model("prop_gas_pump_old3"),
            new Model("prop_vintage_pump")
        };

        #endregion

        #region Constructor

        /// <summary>
        /// Ctor
        /// </summary>
        public FRFuel()
        {
            _hud = new HUD();

            _jerryCanAnimation = new InLoopOutAnimation(
              new Animation(JERRYCAN_ANIM_DICT, "fire_intro"),
              new Animation(JERRYCAN_ANIM_DICT, "fire"),
              new Animation(JERRYCAN_ANIM_DICT, "fire_outro")
            );

            GasStations.LoadGasStations();

            if (GasStations.positions == null)
            {
                Debug.WriteLine("[FRFuel] Gas stations failed to load");

                return;
            }

            _blips = new Blip[GasStations.positions.Length];
            _pickups = new List<Pickup>();

            Exports.Add("addFuel", new AddFuel(ExportsAddFuel));
            Exports.Add("setFuel", new SetFuel(ExportsSetFuel));
            Exports.Add("getCurrentFuelLevel", new GetCurrentFuelLevelDelegate(ExportsGetCurrentFuelLevel));
        }

        #endregion

        #region Tick Handlers

        /// <summary>
        /// On tick
        /// </summary>
        /// <returns></returns>
        [Tick]
        internal async Task OnTick()
        {
            if (!_initialized)
            {
                _initialized = true;

                LoadConfig();

                CreateBlips();

                EntityDecoration.RegisterProperty(FUEL_LEVEL_DECOR, DecorationType.Float);
            }

            await ManageNearbyJerryCanPickUps();

            Ped playerPed = Game.PlayerPed;
            Vehicle vehicle = playerPed.CurrentVehicle;

            if (!PlayerVehicleViableForFuel())
            {
                ManualRefuel(playerPed);

                _currentVehicleFuelLevelInitialized = false;

                return;
            }

            if (_lastVehicle != vehicle)
            {
                _lastVehicle = vehicle;
                _currentVehicleFuelLevelInitialized = false;
            }

            if (!_currentVehicleFuelLevelInitialized)
            {
                InitFuel(vehicle);
            }

            ConsumeFuel(vehicle);
            RenderUI(playerPed);

            await Task.FromResult(0);
        }

        #endregion

        #region Event Handlers

        [EventHandler("frfuel:refuelAllowed")]
        internal void OnRefuelAllowed(bool toggle) => _refuelAllowed = toggle;

        #endregion

        #region Init

        /// <summary>
        /// Loads configuration from file
        /// </summary>
        protected void LoadConfig()
        {
            string configContent = null;

            try
            {
                configContent = LoadResourceFile(GetCurrentResourceName(), "config.ini");
            }
            catch (Exception e)
            {
                Debug.WriteLine($"An error occurred while loading the config file, error description: {e.Message}.");
            }

            Config = new Config(configContent);

            _showHud = Config.Get("ShowHud", "true") == "true";
            _showHudWhenEngineOff = Config.Get("ShowHudWhenEngineOff", "true").ToLower() == "true";

            var fuelConsumptionString = Config.Get("FuelConsumptionRate", "1");
            if (float.TryParse(fuelConsumptionString, out float tmpFuelConsumptionRate))
            {
                FUEL_CONSUMPTION_RATE = tmpFuelConsumptionRate;
            }
#if DEBUG
            else
            {
                Debug.WriteLine("Invalid FuelConsumptionRate value. Make sure it is a valid float value, e.g. 1.2");
            }
#endif

            var refuelRateString = Config.Get("RefuelRate", "1");
            if (float.TryParse(refuelRateString, out float tmpRefuelRate))
            {
                REFUEL_RATE = tmpRefuelRate;
            }
#if DEBUG
            else
            {
                Debug.WriteLine("Invalid RefuelRate value. Make sure it is a valid float value, e.g. 1.2");
            }
#endif

            // if a valid key is set in the config file, set the control.
            if (int.TryParse(Config.Get("EngineToggleKey", "44"), out int tmpControl))
            {
                ENGINE_TOGGLE = (Control)tmpControl;
            }

            NORMAL_HEX = Config.Get("FuelBarNormalColor", NORMAL_HEX).Replace("\"", "").Replace("#", "");
            // normal color
            int r = MathUtil.Clamp(int.Parse(NORMAL_HEX.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int g = MathUtil.Clamp(int.Parse(NORMAL_HEX.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int b = MathUtil.Clamp(int.Parse(NORMAL_HEX.Substring(4, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);

            WARNING_HEX = Config.Get("FuelBarWarningColor", NORMAL_HEX).Replace("\"", "").Replace("#", "");
            // warning color
            int wR = MathUtil.Clamp(int.Parse(WARNING_HEX.Substring(0, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int wG = MathUtil.Clamp(int.Parse(WARNING_HEX.Substring(2, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            int wB = MathUtil.Clamp(int.Parse(WARNING_HEX.Substring(4, 2), System.Globalization.NumberStyles.HexNumber), 0, 255);
            _hud.UpdateBarColors(r, g, b, wR, wG, wB);

#if DEBUG
            Debug.WriteLine($"CreatePickups: {Config.Get("CreatePickups", "true")}");
            Debug.WriteLine($"ShowHud: {Config.Get("ShowHud", "true")}");
            Debug.WriteLine($"EngineToggleKey: {Config.Get("EngineToggleKey", "86")}");
            Debug.WriteLine($"FuelConsumptionRate: {Config.Get("FuelConsumptionRate", "1")}");
#endif
        }

        /// <summary>
        /// Creates blips for gas stations
        /// </summary>
        public void CreateBlips()
        {
            if (Config.Get("CreateBlips", "true") != "true")
            {
                return;
            }

            for (int i = 0; i < GasStations.positions.Length; i++)
            {
                var blip = World.CreateBlip(GasStations.positions[i]);
                blip.Sprite = BlipSprite.JerryCan;
                blip.Color = BlipColor.White;
                blip.Scale = 1f;
                blip.IsShortRange = true;
                blip.Name = "Gas Station";

                _blips[i] = blip;
            }
        }

        /// <summary>
        /// Gets coordinates for jerry cans within 100f radius
        /// </summary>
        /// <param name="position"></param>
        /// <returns>List of coordinates for pickups</returns>
        public IEnumerable<Vector3> GetNearbyJerryCanPickUpCoordinates(Vector3 position) => GasStations.positions.Where(p => p.DistanceToSquared(position) < 100.0f);

        /// <summary>
        /// Automatically adds pickups for nearby jerry cans, and removes when leaving area
        /// </summary>
        public async Task ManageNearbyJerryCanPickUps()
        {
            if (Config.Get("CreatePickups", "true") != "true")
            {
                return;
            }

            Vector3 pos = GetEntityCoords(PlayerPedId(), true);

            int model = 883325847;

            Function.Call(Hash.REQUEST_MODEL, model);

            IEnumerable<Vector3> positions = GetNearbyJerryCanPickUpCoordinates(pos);

            if (positions.Count() == 0 && _pickups.Count != 0)
            {
                _pickups.ForEach(p => p.Delete());
                _pickups.Clear();

                return;
            }

            positions.ToList().ForEach(position =>
            {
                if (!_pickups.Any(pickup => position.DistanceToSquared(pickup.Position) < 5f))
                {
                    // add pickup if one doesn't exist within 5f proximity of it
                    int pickupHandle = CreatePickup(
                        0xc69de3ff, // Petrol Can
                        position.X, position.Y, position.Z - 0.5f,
                        8 | 32, // Place on the ground, local only
                        0,
                        true,
                        (uint)model);

                    Pickup pickup = new Pickup(pickupHandle);

                    _pickups.Add(pickup);
                }
            });

            await Task.FromResult(0);
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Returns vehicle's max fuel level
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public float VehicleMaxFuelLevel(Vehicle vehicle) => GetVehicleHandlingFloat(vehicle.Handle, "CHandlingData", "fPetrolTankVolume");

        /// <summary>
        /// Returns random fuel level between 1/3 and 3/4 of tank capacity
        /// </summary>
        /// <param name="fuelLevel"></param>
        /// <returns></returns>
        public float RandomizeFuelLevel(float fuelLevel)
        {
            float min = fuelLevel / 3f;
            float max = fuelLevel - (fuelLevel / 4);

            return (float)((_random.NextDouble() * (max - min)) + min);
        }

        /// <summary>
        /// Check if the players current vehicle is refuelable
        /// </summary>
        /// <returns></returns>
        protected bool PlayerVehicleViableForFuel()
        {
            Ped playerPed = Game.PlayerPed;
            Vehicle vehicle = playerPed.CurrentVehicle;

            return playerPed.IsInVehicle() && (vehicle.Model.IsCar || vehicle.Model.IsBike || vehicle.Model.IsQuadbike) && vehicle.GetPedOnSeat(VehicleSeat.Driver) == playerPed && vehicle.IsAlive;
        }

        /// <summary>
        /// External API for getting current fuel leve;
        /// </summary>
        /// <returns></returns>
        public float ExportsGetCurrentFuelLevel()
        {
            if (!PlayerVehicleViableForFuel())
            {
                return -1f;
            }

            return Game.PlayerPed.CurrentVehicle.FuelLevel;
        }

        /// <summary>
        /// External API for adding fuel
        /// </summary>
        /// <param name="amount"></param>
        public void ExportsAddFuel(float amount)
        {
            if (PlayerVehicleViableForFuel())
            {
                var vehicle = Game.PlayerPed.CurrentVehicle;

                VehicleSetFuelLevel(vehicle, vehicle.FuelLevel + amount);
            }
        }

        /// <summary>
        /// External API for setting fuel level
        /// </summary>
        /// <param name="amount"></param>
        public void ExportsSetFuel(float amount)
        {
            if (PlayerVehicleViableForFuel())
            {
                var vehicle = Game.PlayerPed.CurrentVehicle;

                VehicleSetFuelLevel(vehicle, amount);
            }
        }

        /// <summary>
        /// Returns gas station position that is in range
        /// </summary>
        /// <param name="pos"></param>
        /// <param name="rangeSquared"></param>
        /// <returns></returns>
        public int GetGasStationIndexInRange(Vector3 pos, float rangeSquared)
        {
            for (int i = 0; i < _blips.Length; i++)
            {
                if (Vector3.DistanceSquared(GasStations.positions[i], pos) < rangeSquared)
                {
                    return i;
                }
            }

            return -1;
        }

        /// <summary>
        /// Get the closest gas pump to the local ped
        /// </summary>
        /// <returns></returns>
        internal Vector3 GetClosestGasPump()
        {
            Vector3 pedPos = Game.PlayerPed.Position;

            foreach (Prop prop in World.GetAllProps().Where(x => x.Position.DistanceToSquared(pedPos) < 1.5f && GAS_PUMP_MODELS.Contains(x.Model)))
            {
                return prop.Position;
            }

            return Vector3.Zero;
        }

        /// <summary>
        /// Checks if vehicle is within range of activation
        /// for any pump at current gas station
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public bool IsVehicleNearAnyPump(Vehicle vehicle)
        {
            Vector3 fuelTankPos = GetVehicleTankPos(vehicle);

            foreach (Vector3 pump in GasStations.pumps[_currentGasStationIndex])
            {
                if (Vector3.DistanceSquared(pump, fuelTankPos) <= 20f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns vehicle's current fuel level
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public float VehicleFuelLevel(Vehicle vehicle)
        {
            if (vehicle.HasDecor(FUEL_LEVEL_DECOR))
            {
                return vehicle.GetDecor<float>(FUEL_LEVEL_DECOR);
            }

            return 65f;
        }

        /// <summary>
        /// Correctly sets vehicle's fuel level
        /// </summary>
        /// <param name="vehicle"></param>
        /// <param name="fuelLevel"></param>
        public void VehicleSetFuelLevel(Vehicle vehicle, float fuelLevel)
        {
            float max = VehicleMaxFuelLevel(vehicle);

            if (fuelLevel > max)
            {
                fuelLevel = max;
            }

            vehicle.FuelLevel = fuelLevel;
            vehicle.SetDecor(FUEL_LEVEL_DECOR, fuelLevel);
        }

        /// <summary>
        /// Inits fuel for given vehicle
        /// </summary>
        /// <param name="vehicle"></param>
        public void InitFuel(Vehicle vehicle)
        {
            _currentVehicleFuelLevelInitialized = true;

            FUEL_TANK_CAPACITY = VehicleMaxFuelLevel(vehicle);

            if (!vehicle.HasDecor(FUEL_LEVEL_DECOR))
            {
                vehicle.SetDecor(FUEL_LEVEL_DECOR, RandomizeFuelLevel(FUEL_TANK_CAPACITY));
            }

            vehicle.FuelLevel = vehicle.GetDecor<float>(FUEL_LEVEL_DECOR);
        }

        /// <summary>
        /// Returns "adequate" vehicle's petrol tank position
        /// </summary>
        /// <param name="vehicle"></param>
        /// <returns></returns>
        public Vector3 GetVehicleTankPos(Vehicle vehicle)
        {
            EntityBone bone = null;

            foreach (string boneName in TANK_BONES)
            {
                int boneIndex = GetEntityBoneIndexByName(vehicle.Handle, boneName);

                bone = vehicle.Bones[boneIndex];

                if (bone.IsValid)
                {
                    break;
                }
            }

            if (bone == null)
            {
                return vehicle.Position;
            }

            return bone.Position;
        }

        /// <summary>
        /// Disable controls related to E for refueling
        /// </summary>
        internal void DisableRefuelControls()
        {
            Game.DisableControlThisFrame(0, Control.Context);
            Game.DisableControlThisFrame(0, Control.VehicleRoof);
            Game.DisableControlThisFrame(0, Control.VehicleHorn);
            Game.DisableControlThisFrame(0, Control.ReplayHidehud);
            Game.DisableControlThisFrame(0, Control.VehicleShuffle);
        }

        /// <summary>
        /// Controls engine
        /// </summary>
        /// <param name="vehicle"></param>
        public void ControlEngine(Vehicle vehicle)
        {
            // all controls related to G
            Game.DisableControlThisFrame(0, ENGINE_TOGGLE);
            Game.DisableControlThisFrame(0, Control.Detonate);
            Game.DisableControlThisFrame(0, Control.PhoneCameraGrid);
            Game.DisableControlThisFrame(0, Control.VehicleFlyUnderCarriage);

            if (!Game.IsDisabledControlJustPressed(0, ENGINE_TOGGLE) || Game.IsControlPressed(0, Control.Context))
            {
                return;
            }

            if (vehicle.IsEngineRunning)
            {
                vehicle.IsDriveable = false;

                SetVehicleEngineOn(vehicle.Handle, false, true, true);

                return;
            }

            vehicle.IsDriveable = true;
            vehicle.IsEngineRunning = true;
        }

        /// <summary>
        /// Renders fuel bar and marker
        /// </summary>
        /// <param name="playerPed"></param>
        public void RenderUI(Ped playerPed)
        {
            int gasStationIndex = GetGasStationIndexInRange(playerPed.Position, SHOW_MARKER_RANGE);

            if (gasStationIndex != -1 && gasStationIndex != _currentGasStationIndex)
            {
                _currentGasStationIndex = gasStationIndex;
            }

            if (gasStationIndex == -1 && _currentGasStationIndex != -1)
            {
                _currentGasStationIndex = -1;
            }

            if (!_showHud || !IsHudPreferenceSwitchedOn())
            {
                return;
            }

            Vehicle currentVeh = playerPed.CurrentVehicle;

            bool nearPump = _currentGasStationIndex != -1;
            bool engineRunning = currentVeh.IsEngineRunning;

            if (!nearPump && !engineRunning && !_showHudWhenEngineOff)
            {
                return;
            }

            _hud.RenderBar(currentVeh.FuelLevel, FUEL_TANK_CAPACITY);
        }

        /// <summary>
        /// Handles manual vehicle refueling using Jerry Can
        /// </summary>
        /// <param name="playerPed"></param>
        public void ManualRefuel(Ped playerPed)
        {
            if (playerPed.Weapons.Current.Hash != WeaponHash.PetrolCan)
            {
                return;
            }

            Vector3 pos = playerPed.Position;

            if (!IsAnyVehicleNearPoint(pos.X, pos.Y, pos.Z, 3f))
            {
                return;
            }

            Vehicle vehicle = World.GetAllVehicles().OrderBy(v => v.Position.DistanceToSquared(pos)).First();

            if (
                vehicle != null &&
                vehicle.Exists() &&
                vehicle.HasDecor(FUEL_LEVEL_DECOR)
            )
            {
                float max = VehicleMaxFuelLevel(vehicle);
                float current = VehicleFuelLevel(vehicle);

                if (max - current < 0.5f)
                {
                    Screen.DisplayHelpTextThisFrame("Fuel tank is full");
                }
                else
                {
                    Screen.DisplayHelpTextThisFrame("Manual refueling");
                }

                if (!Game.IsControlPressed(0, Control.Attack))
                {
                    return;
                }

                _jerryCanAnimation.Magick(playerPed);

                if (current < max)
                {
                    if (current + 0.1f >= max)
                    {
                        VehicleSetFuelLevel(vehicle, max);
                    }
                    else
                    {
                        VehicleSetFuelLevel(vehicle, current + 0.2f);
                    }
                }

                if (Game.IsControlJustReleased(0, Control.VehicleAttack))
                {
                    _jerryCanAnimation.RewindAndStop(playerPed);
                }

                return;
            }
        }

        /// <summary>
        /// Processes fuel consumption
        /// </summary>
        /// <param name="vehicle"></param>
        public void ConsumeFuel(Vehicle vehicle)
        {
            float fuel = VehicleFuelLevel(vehicle);

            // Consuming
            if (fuel > 0 && vehicle.IsEngineRunning)
            {
                float normalizedRPMValue = (float)Math.Pow(vehicle.CurrentRPM, 1.5);
                float consumedFuel = 0f;

                consumedFuel += normalizedRPMValue * FUEL_RPM_IMPACT;
                consumedFuel += vehicle.Acceleration * FUEL_ACCELERATION_IMPACT;
                consumedFuel += vehicle.MaxTraction * FUEL_TRACTION_IMPACT;

                fuel -= consumedFuel * FUEL_CONSUMPTION_RATE;
                fuel = fuel < 0f ? 0f : fuel;
            }

            // Refueling at gas station
            if (_currentGasStationIndex == -1 || !IsVehicleNearAnyPump(vehicle))
            {
                VehicleSetFuelLevel(vehicle, fuel);

                return;
            }

            if (vehicle.Speed < 0.1f)
            {
                ControlEngine(vehicle);
            }

            if (vehicle.IsEngineRunning)
            {
                Screen.DisplayHelpTextThisFrame("~INPUT_THROW_GRENADE~ Turn off engine");

                return;
            }

            Screen.DisplayHelpTextThisFrame("~INPUT_CONTEXT~ Refuel\n~INPUT_THROW_GRENADE~ Turn on engine");

            if (!_refuelAllowed)
            {
                return;
            }

            DisableRefuelControls();

            if (Game.IsDisabledControlJustReleased(0, Control.Context) && ADDED_FUEL_CAPACITOR > 0f)
            {
                TriggerEvent("frfuel:fuelAdded", ADDED_FUEL_CAPACITOR);
                TriggerServerEvent("frfuel:fuelAdded", ADDED_FUEL_CAPACITOR);
#if DEBUG
                Screen.ShowNotification($"~g~Refueled {fuel:0.0}/{FUEL_TANK_CAPACITY:0.0}L");
#endif
                ADDED_FUEL_CAPACITOR = 0f;
            }

            if (!Game.IsDisabledControlPressed(0, Control.Context))
            {
                return;
            }

            if (fuel < FUEL_TANK_CAPACITY)
            {
                float fuelPortion = 0.1f * REFUEL_RATE;

                fuel += fuelPortion;
                ADDED_FUEL_CAPACITOR += fuelPortion;
            }

            VehicleSetFuelLevel(vehicle, fuel); 
        }

        #endregion
    }
}
