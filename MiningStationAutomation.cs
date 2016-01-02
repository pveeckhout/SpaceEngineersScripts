﻿using Sandbox.ModAPI.Ingame;
using Sandbox.ModAPI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SpaceEngineersScripts
{
    class MiningStationAutomation
    {
        #region programming environment essential inits, DO NOT COPY TO GAME
        IMyGridTerminalSystem GridTerminalSystem;
        IMyProgrammableBlock Me { get; }
        private void Echo(string message) { }
        #endregion

        /*
        *
        * COPY FROM HERE
        *
        */

        #region Proggrammable block script
        #region Config
        //OPERATIONAL
        const float ROTOR_RPM = 0.5f;
        const float DRILL_DOWN_SPEED = 0.006f;
        readonly List<float> DRILL_RADII = new List<float>() { 0f, 3.5f, 7f, 10f }; //Drills can technically do a 5 wide trench, to be sure nu small floating rocks are left, do smaller intervals.
        const bool DEBUG = true;
        const bool FORCEROTOR_TORQUE = true;
        const bool INIT_FLATTENING = true; // safety precaoution
        const bool END_FLATTENING = true; //flatten pit bottom to allow cars to drive more easily;
        //BLOCK SETUP
        const string ROTOR_NAME = "Drill Rotor";
        const string H_PISTON_NAME = "Horizontal Piston";
        const string V_PISTON_NAME = "Vertical Piston";
        const string DRILL_STATION_NAME = "Drill Station";
        const string DEBUG_PANEL_NAME = "Debug Panel";
        const float TARGET_DEPTH = 20f;
        #endregion

        private IMyPistonBase HorizontalPiston { get; set; }
        private List<IMyPistonBase> VerticalPistons { get; set; }
        private List<IMyShipDrill> Drills { get; set; }
        private IMyMotorAdvancedStator Rotor { get; set; }
        private List<IMyRadioAntenna> Antennas { get; set; }
        private List<IMyTextPanel> OutputPanels { get; set; }
        private List<IMyTerminalBlock> Refineries { get; set; }
        private List<IMyTerminalBlock> CargoContainers { get; set; }


        private bool movingToPosition = false;
        private int currentCircle = -1;
        private bool initflattening = false;
        private bool endflattening = false;
        private bool drillingDone = false;
        private Dictionary<string, string> initArgs;

        //TODO: fix drill index issue;
        //TODO do end flattening
        void Main(string argument)
        {
            //check if all blocks are initialized
            if (!(HorizontalPiston != null && VerticalPistons != null && Drills != null && Rotor != null))
            {
                Init(argument);
            }

            if (movingToPosition)
            {
                ToCurrentCircleDrillRadiusStartPosition();
                return;
            }

            if (initflattening)
            {
                FlatteninCircles();
                return;
            }

            if (!drillingDone)
            {
                if (!movingToPosition)
                    Drill();

                CLeanRefineries();
            }
            else
            {
                if (endflattening)
                {
                    FlatteninCircles(TARGET_DEPTH);
                    //the we are doen with the endflattening
                    if (currentCircle < 0)
                    {
                        endflattening = false;
                    }
                        
                    return;
                }
                else {
                    currentCircle = -1;
                    ToCurrentCircleDrillRadiusStartPosition();
                    if (!movingToPosition)
                        Shutdown();
                }
            }
        }

        void Shutdown()
        {
            //clean the refineries input and output and stop them
            CLeanRefineries(0);
            CLeanRefineries(1);

            Refineries.ForEach(refinery => refinery.GetActionWithName("OnOff_Off").Apply(refinery));

            //turn off drills
            Drills.ForEach(drill => drill.GetActionWithName("OnOff_Off").Apply(drill));

            //stop and turn of rotor
            setRotorSpeed(0f);
            Rotor.GetActionWithName("OnOff_Off").Apply(Rotor);

            //turn off pistons
            HorizontalPiston.GetActionWithName("OnOff_Off").Apply(HorizontalPiston);
            VerticalPistons.ForEach(piston => piston.GetActionWithName("OnOff_Off").Apply(piston));

            //turn off the debug panels
            if (OutputPanels != null)
                OutputPanels.ForEach(panel => panel.GetActionWithName("OnOff_Off").Apply(panel));

            SetStatusToAntenna("DONE", false, false);

            //turn off timers
            var timers = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyTimerBlock>(timers);
            timers.ForEach(timer => timer.GetActionWithName("OnOff_Off").Apply(timer));

            //turn off self
            Me.GetActionWithName("OnOff_Off").Apply(Me);
        }

        void CLeanRefineries(int refineryTargetInventoryIndex = 1)
        {
            ClearDebug();

            OutputToDebug("cleaning refineries started");

            //get Refineries
            if (Refineries == null)
                Refineries = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyRefinery>(Refineries);

            //get Cargo Containers
            if (CargoContainers == null)
                CargoContainers = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyCargoContainer>(CargoContainers);

            foreach (var refinery in Refineries)
            {
                //get the target inventory, 0 is input, 1 is output, else will probably throw error, CBA to check on this
                var refineryInventory = refinery.GetInventory(refineryTargetInventoryIndex);
                var refineryItems = refineryInventory.GetItems();
                IMyInventory targetInventory = null;

                //if there are items in the refinery
                if (refineryItems.Count > 0)
                {
                    //revers loop as we will remove items
                    for (int i = refineryItems.Count - 1; i >= 0; i--)
                    {
                        //loop the containers to find a target
                        foreach (var cargoContainer in CargoContainers)
                        {
                            //if the container is operational and not full it is a valid target
                            if ((cargoContainer.IsFunctional || cargoContainer.IsWorking) && !cargoContainer.GetInventory(0).IsFull)
                            {
                                targetInventory = cargoContainer.GetInventory(0);
                                break;
                            }
                        }

                        // transfer items if target is found
                        if (targetInventory != null)
                            refineryInventory.TransferItemTo(targetInventory, i);
                    }
                }
            }

            OutputToDebug("cleaning refineries ended");
        }

        void Drill()
        {
            ClearDebug();

            //check for init  conditions
            if (currentCircle < 0)
            {
                SetStatusToAntenna("DRILLING-INIT");

                if (DEBUG)
                    OutputToDebug("Drill sequece, init");

                //move to the start position
                ToCurrentCircleDrillRadiusStartPosition();

                //if to start is false, the we are at the starting position, set the current index to 0
                if (!movingToPosition)
                {
                    //ínit is done
                    currentCircle = 0;

                    //remove rotor limits
                    SetRotorLimits(float.NegativeInfinity, float.PositiveInfinity);

                    //Move the horizontal piston to the circle defined
                    if (DEBUG)
                        OutputToDebug(string.Format("trying to access DRILL_RADII witj index {0} [1]", currentCircle));

                    MovePistonToPosition(HorizontalPiston, DRILL_RADII[currentCircle]);

                    //start the rotor
                    setRotorSpeed(ROTOR_RPM);

                    if (DEBUG)
                        OutputToDebug("Drill sequece init done");
                }
            }
            else
            {
                Echo(string.Format("staring drilling circle {0}/{1}, do currentCurcle++ and performing ToStart()", currentCircle + 1, DRILL_RADII.Count));

                if (currentCircle < DRILL_RADII.Count)
                {
                    SetStatusToAntenna(string.Format("DRILLING {0}/{1}", currentCircle + 1, DRILL_RADII.Count));

                    //start the rotor
                    setRotorSpeed(ROTOR_RPM);

                    if (DEBUG)
                        OutputToDebug(string.Format("Drilling circle {0}", currentCircle));

                    //Move the horizontal piston to the circle defined
                    if (DEBUG)
                        OutputToDebug(string.Format("trying to access DRILL_RADII witj index {0} [2]", currentCircle));

                    MovePistonToPosition(HorizontalPiston, DRILL_RADII[currentCircle]);

                    //move the vertical positions
                    if (DEBUG)
                        OutputToDebug(string.Format("Target depth: {0}\nCurrent depth: {1}", TARGET_DEPTH, GetPistonsTotalPosition(VerticalPistons)));

                    MovePistonToPosition(VerticalPistons, TARGET_DEPTH, DRILL_DOWN_SPEED);

                    if (TARGET_DEPTH <= GetPistonsTotalPosition(VerticalPistons))
                    {
                        if (DEBUG)
                            OutputToDebug("All pistons reached max depth, returning to start position");

                        Echo(string.Format("Done drilling circle {0}/{1}, do currentCurcle++ and performing ToStart()", currentCircle + 1, DRILL_RADII.Count));

                        currentCircle++;

                        if (!(currentCircle >= DRILL_RADII.Count))
                        {
                            ToCurrentCircleDrillRadiusStartPosition();
                        }
                    }
                }
                else
                {
                    OutputToDebug("Drill sequece, closure");
                    SetStatusToAntenna(string.Format("DRILLING-END"));

                    Echo(string.Format("Done drilling, move to position [0,0,0]"));
                    //move to the start position
                    ToPosition(0, 0, 0);

                    //if to start is false, the we are at the starting position, turn off all unneeded devices to concerve energy
                    if (!movingToPosition)
                    {
                        Echo(string.Format("Done drilling, move to start of current circle -1"));
                        SetRotorLimits(float.NegativeInfinity, float.PositiveInfinity);
                        OutputToDebug("Drill sequece done");

                        currentCircle = -1;

                        //TODO: turn off everything nonvital
                        drillingDone = true;
                    }
                }
            }
        }

        void FlatteninCircles(float depth = 0f)
        {
            //clear the output screen
            ClearDebug();

            if (DEBUG)
            {
                OutputToDebug("Drilling the flattening rounds");
                OutputToDebug(string.Format("Current circle: {0}", currentCircle));
            }

            SetStatusToAntenna("FLATTENING", false);

            var currentRotorposition = GetRotorPosition();

            //turn the rotor to zero to if the current circle is -1
            if (currentCircle < 0 || depth != GetPistonsTotalPosition(VerticalPistons))
            {
                if (DEBUG)
                    OutputToDebug("init flattening rounds, moving rotor to start");

                //debug outs happen in the method
                MoveRotorToPosition(-180f);

                MovePistonToPosition(VerticalPistons, depth);
            }

            currentRotorposition = GetRotorPosition();

            if (DEBUG)
                OutputToDebug(string.Format("Current rotor position: {0}", currentRotorposition));

            /*start a new circle
            *
            * if the rotor position is 0 start a new circle by
            * 1) incrementing the current circle (first pass will invrement from -1 to 0)
            * 2) moving the hPiston to the respective position
            * 3) reversing the rotor so the the radius gets drilled the first time
            *
            * We need to check to prevent en index out of bounds. if that woudl happen, then we end the safety rounds.
            */
            if (currentRotorposition <= -180 && depth == GetPistonsTotalPosition(VerticalPistons))
            {
                currentCircle++;

                if (DEBUG)
                    OutputToDebug(string.Format("Current circle: {0}", currentCircle));

                //full cricle, cannot use 360 or as SE will interpret it as +infinity
                // idem for negativ values
                SetRotorLimits(-180, 180);

                //if current circle > circles defined go to back to start and safty circles are done
                if (currentCircle >= DRILL_RADII.Count)
                {
                    //set the safety rounds to false as we are done and never will have to do it again
                    initflattening = false;

                    //set the current position back to -1 (init state of each step)
                    currentCircle = -1;

                    //stop the rotor
                    setRotorSpeed(0f);

                    //Move the horizontal piston to the start circle
                    MovePistonToPosition(HorizontalPiston, DRILL_RADII[0]);

                    if (DEBUG)
                        OutputToDebug("Ended flattening Circles");

                    return;
                }

                //Move the horizontal piston to the circle defined
                if (DEBUG)
                    OutputToDebug(string.Format("trying to access DRILL_RADII witj index {0} [3]", currentCircle));

                MovePistonToPosition(HorizontalPiston, DRILL_RADII[currentCircle]);

                setRotorSpeed(ROTOR_RPM);
            }

            //reverse the drills so the radius gets drilled the second time
            if (currentRotorposition >= 180 && depth == GetPistonsTotalPosition(VerticalPistons))
            {
                setRotorSpeed(-ROTOR_RPM);
            }

            if (DEBUG)
                OutputToDebug(string.Format("currentCircle: {0}", currentCircle));
        }

        bool ToPosition(float width, float depth, float rotation)
        {
            bool working = false;

            ClearDebug();

            if (DEBUG)
            {
                OutputToDebug(string.Format("moving to position width: {0}, depth: {1}, rotation: {2}", width, depth, rotation));
                OutputToDebug(string.Format("Action in progress: {0}", working));
                OutputToDebug("stopping rotor and turning on drills");
            }

            setRotorSpeed(0f);

            //If the drills ar not on, turn on
            Drills.ForEach(drill => drill.GetActionWithName("OnOff_On").Apply(drill));

            if (DEBUG)
                OutputToDebug(string.Format("Action in progress: {0}", working));

            //While the vPistons are not in position, move
            {
                if (GetPistonsTotalPosition(VerticalPistons) != depth)
                {
                    MovePistonToPosition(VerticalPistons, depth);
                    working = true;
                }
            }
            if (DEBUG)
                OutputToDebug(string.Format("Action in progress: {0}", working));

            //if working, stop method
            if (working)
                return working;

            //When the hPistons are retracted, retract the hPiston
            if (HorizontalPiston.CurrentPosition != 0)
            {
                MovePistonToPosition(HorizontalPiston, width);
                working = true;
            }

            if (DEBUG)
                OutputToDebug(string.Format("Action in progress: {0}", working));

            //if working, stop method
            if (working)
                return working;

            //move rotor to position
            if (GetRotorPosition() != rotation)
            {
                MoveRotorToPosition(rotation);
                working = true;
            }

            if (DEBUG)
                OutputToDebug(string.Format("reached end of ToPosition, work in progress: {0}", working));

            movingToPosition = working;

            return movingToPosition;
        }

        void ToCurrentCircleDrillRadiusStartPosition()
        {
            var working = false;

            if (currentCircle > -1)
            {
                working = ToPosition(DRILL_RADII[currentCircle], 0, 0);
            }
            else
            {
                working = ToPosition(0, 0, 0);
            }

            movingToPosition = working;
        }

        void Init(string argument)
        {
            if (DEBUG)
            {
                ClearDebug();
                OutputToDebug("INIT: clearing text");
            }

            SetStatusToAntenna("INIT-Start", false, false);

            string outval = "";
            bool found = false;
            initArgs = new Dictionary<string, string>();

            if (DEBUG)
                OutputToDebug("parsing input args");

            foreach (var keyPair in argument.Split(new string[] { "|" }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] split = keyPair.Split('=');
                initArgs.Add(split[0], split[1]);
            }

            //set done to false, DUUUHH
            drillingDone = false;
            //move drills to start after init
            movingToPosition = true;

            //ground level flattening
            found = initArgs.TryGetValue("initflattening", out outval);

            if (found)
            {
                initflattening = bool.Parse(outval);
                if (DEBUG)
                    OutputToDebug(string.Format("input arg groundlevelflattening found: {0}", initflattening));
            }
            else
            {
                initflattening = INIT_FLATTENING;
            }

            //bottom flattening
            found = initArgs.TryGetValue("endflattening", out outval);

            if (found)
            {
                endflattening = bool.Parse(outval);
                if (DEBUG)
                    OutputToDebug(string.Format("input arg skipsafetycircles found: {0}", endflattening));
            }
            else
            {
                endflattening = END_FLATTENING;
            }

            //reset the current circle index to init state
            found = initArgs.TryGetValue("currentcircle", out outval);
            if (found)
            {
                currentCircle = int.Parse(outval);
                if (DEBUG)
                    OutputToDebug(string.Format("input arg currentcircle found: {0}", currentCircle));
            }
            else
            {
                currentCircle = -1;
            }

            if (DEBUG)
                OutputToDebug("Initializing blocks");

            HorizontalPiston = GridTerminalSystem.GetBlockWithName(H_PISTON_NAME) as IMyPistonBase;
            Rotor = GridTerminalSystem.GetBlockWithName(ROTOR_NAME) as IMyMotorAdvancedStator;

            if (DEBUG)
            {
                OutputToDebug("found the folowing:");
                OutputToDebug(string.Format("horizontal piston: {0}\nRotor: {1}", HorizontalPiston != null, Rotor != null));
            }

            if (FORCEROTOR_TORQUE)
                ForceRotorsTorque();

            if (DEBUG)
            {
                OutputToDebug("Current Rotor limits");
                OutputToDebug(string.Format("Lower: {0}, Upper: {1}", Rotor.LowerLimit, Rotor.UpperLimit));
            }

            VerticalPistons = new List<IMyPistonBase>();
            Drills = new List<IMyShipDrill>();

            var pistonTempList = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyPistonBase>(pistonTempList);

            pistonTempList.ForEach(vPiston => { if (vPiston.CustomName.Contains(V_PISTON_NAME)) { VerticalPistons.Add(vPiston as IMyPistonBase); } });

            var drillTempList = new List<IMyTerminalBlock>();
            GridTerminalSystem.GetBlocksOfType<IMyShipDrill>(drillTempList);

            drillTempList.ForEach(drill => Drills.Add(drill as IMyShipDrill));

            if (DEBUG)
                OutputToDebug(string.Format("#vertical pistons: {0}\n#drils: {1}", VerticalPistons.Count, Drills.Count));

            SetStatusToAntenna("INIT-Done", false, false);
        }

        void OutputToDebug(string text)
        {
            if (OutputPanels == null || OutputPanels.Count == 0)
            {
                OutputPanels = new List<IMyTextPanel>();
                var panelTempList = new List<IMyTerminalBlock>();
                GridTerminalSystem.GetBlocksOfType<IMyTextPanel>(panelTempList);

                panelTempList.ForEach(panel => { if (panel.CustomName.Contains(DEBUG_PANEL_NAME)) { (panel as IMyTextPanel).WritePublicTitle("DEBUG"); OutputPanels.Add(panel as IMyTextPanel); } });
            }

            if (OutputPanels != null)
            {
                if (!text.EndsWith("\n"))
                    text += "\n";

                OutputPanels.ForEach(panel => panel.WritePublicText(panel.GetPublicText() + text));
            }
        }

        void ClearDebug()
        {
            if (OutputPanels == null || OutputPanels.Count == 0)
            {
                OutputPanels = new List<IMyTextPanel>();
                var panelTempList = new List<IMyTerminalBlock>();
                GridTerminalSystem.GetBlocksOfType<IMyTextPanel>(panelTempList);

                panelTempList.ForEach(panel => { if (panel.CustomName.Contains(DEBUG_PANEL_NAME)) { OutputPanels.Add(panel as IMyTextPanel); } });
            }

            OutputPanels.ForEach(panel => { panel.WritePublicText(""); panel.WritePublicTitle("DEBUG"); });
        }

        void SetStatusToAntenna(string status, bool showPercentage = true, bool showEta = true)
        {
            if (DEBUG)
                OutputToDebug(string.Format("Setting {0} status to the antennas", status));

            if (Antennas == null || Antennas.Count == 0)
            {
                Antennas = new List<IMyRadioAntenna>();
                var antennaTempList = new List<IMyTerminalBlock>();
                GridTerminalSystem.GetBlocksOfType<IMyRadioAntenna>(antennaTempList);

                antennaTempList.ForEach(antenna => Antennas.Add(antenna as IMyRadioAntenna));
            }

            string antennaName = "{0} - {1}";
            if (showPercentage)
                antennaName += " ({2}%)";
            if (showEta)
                antennaName += " [{3}]";

            if (Antennas != null)
            {
                Antennas.ForEach(antenna => antenna.SetCustomName(string.Format(antennaName, DRILL_STATION_NAME, status, getPercentageDone(), GetETA())));
            }

            if (DEBUG)
                OutputToDebug("Done setting status to the antennas");
        }

        float GetPistonsTotalPosition(List<IMyPistonBase> pistons)
        {

            if (DEBUG)
                OutputToDebug("Calculating total position of pistons");

            var total = 0f;

            pistons.ForEach(piston => total += piston.CurrentPosition);

            if (DEBUG)
                OutputToDebug("Done Calculating total position of pistons");

            return total;
        }

        float GetRotorPosition()
        {
            var currentposition = "";

            System.Text.RegularExpressions.Regex matchthis = new System.Text.RegularExpressions.Regex(@"^.+\n.+\:\s?(-?[0-9]+).*[\s\S]*$");
            System.Text.RegularExpressions.Match match = matchthis.Match(Rotor.DetailedInfo);
            if (match.Success)
            {
                currentposition = match.Groups[1].Value;
            }
            else
            {
                OutputToDebug("ERROR: The main rotor position could not parsed");
                OutputToDebug("ERROR: script stopped");
                throw new FormatException("The rotor position could not parsed");
            }
            return float.Parse(currentposition);
        }

        void ForceRotorsTorque()
        {
            if (DEBUG)
                OutputToDebug("Forcing rotor torque");

            Rotor.SetValueFloat("BrakingTorque", 36000000);
            Rotor.SetValueFloat("Torque", 10000000);
        }

        void MoveRotorToPosition(float destinationPosition, float rpm = ROTOR_RPM)
        {

            var currentPosition = GetRotorPosition();

            if (DEBUG)
            {
                OutputToDebug("Rotor Current rotor position: " + currentPosition);
                OutputToDebug("Rotor Destination rotor position: " + destinationPosition);
                OutputToDebug("Rotor current LowerLimit: " + Rotor.GetValueFloat("LowerLimit"));
                OutputToDebug("Rotor current UpperLimit: " + Rotor.GetValueFloat("UpperLimit"));
            }

            //set the limits
            Rotor.SetValueFloat("LowerLimit", destinationPosition);
            Rotor.SetValueFloat("UpperLimit", destinationPosition);

            //move the rotor to within the limits
            if (currentPosition == destinationPosition)
            {
                setRotorSpeed(0f);
                Rotor.GetActionWithName("OnOff_Off").Apply(Rotor); // Stop rotor
                if (DEBUG)
                    OutputToDebug("position reached, turning off");
            }
            else if (currentPosition < destinationPosition)
            {
                Rotor.GetActionWithName("OnOff_On").Apply(Rotor); // Start rotor
                OutputToDebug("Rotor - currentPosition < destinationPosition");
                setRotorSpeed(rpm);
            }
            else if (currentPosition > destinationPosition)
            {
                Rotor.GetActionWithName("OnOff_On").Apply(Rotor); // Start rotor 
                OutputToDebug("Rotor - currentPosition > destinationPosition");
                setRotorSpeed(-rpm);
            }
        }

        void SetRotorLimits(float lower, float upper)
        {
            if (DEBUG)
            {
                OutputToDebug("Setting Rotor limits");
                OutputToDebug(string.Format("Rotor current limit: [{0},{1}]", Rotor.GetValueFloat("LowerLimit"), Rotor.GetValueFloat("UpperLimit")));
                OutputToDebug(string.Format("setting to [{0},{1}]", lower, upper));
            }

            //warn for fuckery if settin values possible out of bounds when not obviously meant to be that way
            if ((lower < -360 && lower != float.NegativeInfinity) || (upper > 360 && upper != float.PositiveInfinity))
            {
                Echo("[WARN] Setting Rotor limits is doing wierd stuff around or beyond the 360 degree mark, often SE interprets this as infinity");
                OutputToDebug("[WARN] Setting Rotor limits is doing wierd stuff around or beyond the 360 degree mark, often SE interprets this as infinity");
            }

            Rotor.SetValueFloat("LowerLimit", lower);
            Rotor.SetValueFloat("UpperLimit", upper);
        }

        void setRotorSpeed(float rpm)
        {
            if (DEBUG)
                OutputToDebug(string.Format("Setting Rotor speed to: {0}", rpm));

            Rotor.SetValueFloat("Velocity", rpm);
            Rotor.GetActionWithName("OnOff_On").Apply(Rotor); // Start rotor
        }

        void RemoveRotorLimits()
        {
            if (DEBUG)
                OutputToDebug("Resetting Rotor limits");

            SetRotorLimits(float.NegativeInfinity, float.PositiveInfinity);
        }

        void MovePistonToPosition(List<IMyPistonBase> pistons, float destPosition, float speed = 0.5f)
        {
            if (DEBUG)
            {
                OutputToDebug(string.Format("Moving #{0} pistons to combined position: {1}", pistons.Count, destPosition));
                var combinedPosition = 0f;
                pistons.ForEach(piston => combinedPosition += piston.CurrentPosition);

                OutputToDebug(string.Format("Current combined position: {0}", combinedPosition));
            }

            pistons.ForEach(piston => MovePistonToPosition(piston, destPosition / (float)pistons.Count, speed / (float)pistons.Count));
        }

        void MovePistonToPosition(IMyPistonBase piston, float destPosition, float speed = 0.5f)
        {
            if (DEBUG)
            {
                OutputToDebug(string.Format("Moving single piston to: {0}", destPosition));
                OutputToDebug(string.Format("Current position: {0}", piston.CurrentPosition));
            }

            piston.SetValueFloat("LowerLimit", destPosition);
            piston.SetValueFloat("UpperLimit", destPosition);

            if (piston.CurrentPosition > destPosition)
            {
                piston.SetValueFloat("Velocity", -speed);
            }
            else
            {
                piston.SetValueFloat("Velocity", speed);
            }
        }

        string GetETA()
        {
            if (DEBUG)
                OutputToDebug("Calculating ETA");

            try
            {
                var seconds = 0f;

                if (initflattening)
                {
                    seconds = ((DRILL_RADII.Count - currentCircle) * 2) / (ROTOR_RPM / 60) + (TARGET_DEPTH / DRILL_DOWN_SPEED) * DRILL_RADII.Count;
                }
                else
                {
                    seconds = (TARGET_DEPTH / DRILL_DOWN_SPEED) * (DRILL_RADII.Count - currentCircle);
                }

                if (DEBUG)
                    OutputToDebug("Done Calculating ETA");

                return string.Format("~ {0}m", Math.Round(seconds / 60, MidpointRounding.AwayFromZero));
            }
            catch (Exception)
            {
                if (DEBUG)
                    OutputToDebug("Calculating ETA ended with error, returning 'UNKNOWN'");

                return "UNKNOWN";
            }
        }
        float getPercentageDone()
        {
            if (DEBUG)
                OutputToDebug("Calculating Percentage Done");

            if (VerticalPistons != null)
            {
                var percentageDone = 0f;

                if (initflattening)
                {
                    percentageDone = 0;
                }
                else
                {
                    percentageDone = 100 * ((currentCircle + 1) * GetPistonsTotalPosition(VerticalPistons)) / (DRILL_RADII.Count * TARGET_DEPTH);
                }

                if (DEBUG)
                    OutputToDebug("finished Calculating Percentage Done");

                return percentageDone;
            }
            else
            {
                if (DEBUG)
                    OutputToDebug("Calculating Percentage could not be done, returning 0");

                return 0;
            }

        }
        #endregion

        /*
        *
        * COPY TO HERE
        *
        */
    }
}