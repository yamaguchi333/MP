﻿using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using com.drew.imaging.jpg;
using com.drew.metadata;
using com.drew.metadata.exif;
using GeoAPI.CoordinateSystems;
using GeoAPI.CoordinateSystems.Transformations;
using GMap.NET;
using GMap.NET.WindowsForms;
using GMap.NET.WindowsForms.Markers;
using log4net;
using MissionPlanner.ArduPilot;
using MissionPlanner.GCSViews;
using MissionPlanner.Maps;
using MissionPlanner.Utilities;
using ProjNet.CoordinateSystems;
using ProjNet.CoordinateSystems.Transformations;

namespace MissionPlanner.Grid
{
    public partial class GridUI : Form
    {
        private static readonly ILog log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        // Variables
        const double rad2deg = (180 / Math.PI);
        const double deg2rad = (1.0 / rad2deg);

        private GridPlugin plugin;
        static public Object thisLock = new Object();

        GMapOverlay routesOverlay;
        GMapOverlay kmlpolygonsoverlay;
        List<PointLatLngAlt> list = new List<PointLatLngAlt>();
        List<PointLatLngAlt> grid;
        bool loadedfromfile = false;
        bool loading = false;

        Dictionary<string, camerainfo> cameras = new Dictionary<string, camerainfo>();

        public string DistUnits = "";
        public string inchpixel = "";
        public string feet_fovH = "";
        public string feet_fovV = "";

        internal PointLatLng MouseDownStart = new PointLatLng();
        internal PointLatLng MouseDownEnd;
        internal PointLatLngAlt CurrentGMapMarkerStartPos;
        PointLatLng currentMousePosition;
        GMapMarker marker;
        GMapMarker CurrentGMapMarker = null;
        GMapMarkerOverlapCount GMapMarkerOverlap = new GMapMarkerOverlapCount(PointLatLng.Empty);
        int CurrentGMapMarkerIndex = 0;
        bool isMouseDown = false;
        bool isMouseDraging = false;

        Color grid_color = Color.Red;    // @eams add
        int grid_type = 2;  // @eams add
        double area_unit = 5.0;  // @eams add

        // GridUI
        public GridUI(GridPlugin plugin)
        {
            this.plugin = plugin;

            InitializeComponent();

            loading = true;

            map.MapProvider = plugin.Host.FDMapType;
            map.MaxZoom = plugin.Host.FDGMapControl.MaxZoom;
            TRK_zoom.Maximum = map.MaxZoom;

            kmlpolygonsoverlay = new GMapOverlay("kmlpolygons");
            map.Overlays.Add(kmlpolygonsoverlay);

            routesOverlay = new GMapOverlay("routes");
            map.Overlays.Add(routesOverlay);

            // Map Events
            map.OnMapZoomChanged += new MapZoomChanged(map_OnMapZoomChanged);
            map.OnMarkerEnter += new MarkerEnter(map_OnMarkerEnter);
            map.OnMarkerLeave += new MarkerLeave(map_OnMarkerLeave);
            map.MouseUp += new MouseEventHandler(map_MouseUp);

            map.OnRouteEnter += new RouteEnter(map_OnRouteEnter);
            map.OnRouteLeave += new RouteLeave(map_OnRouteLeave);

            var points = plugin.Host.FPDrawnPolygon;
            points.Points.ForEach(x => { list.Add(x); });
            points.Dispose();
            if (plugin.Host.config["distunits"] != null)
                DistUnits = plugin.Host.config["distunits"].ToString();

            CMB_startfrom.DataSource = Enum.GetNames(typeof(Utilities.Grid.StartPosition));
            CMB_startfrom.SelectedIndex = 0;

            // set and angle that is good
            NUM_angle.Value = (decimal)((getAngleOfLongestSide(list) + 360) % 360);
            TXT_headinghold.Text = (Math.Round(NUM_angle.Value)).ToString();
            TXT_angle.Text = (Math.Round(NUM_angle.Value)).ToString();    // @eams add

            if (plugin.Host.cs.firmware == Firmwares.ArduPlane)
                NUM_UpDownFlySpeed.Value = (decimal)(12 * CurrentState.multiplierspeed);

            map.MapScaleInfoEnabled = true;
            map.ScalePen = new Pen(Color.Orange);

            foreach (var temp in FlightData.kmlpolygons.Polygons)
            {
                kmlpolygonsoverlay.Polygons.Add(new GMapPolygon(temp.Points, "") { Fill = Brushes.Transparent });
            }
            foreach (var temp in FlightData.kmlpolygons.Routes)
            {
                kmlpolygonsoverlay.Routes.Add(new GMapRoute(temp.Points, ""));
            }

            xmlcamera(false, Settings.GetRunningDirectory() + "camerasBuiltin.xml");

            xmlcamera(false, Settings.GetUserDataDirectory() + "cameras.xml");

            loading = false;


            // @eams add / form max/min button are invisible
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            // @eams add / grid line color setting
            if (plugin.Host.config["grid_color"] != null)
                grid_color = ColorTranslator.FromHtml(plugin.Host.config["grid_color"].ToString());

            // @eams add / grid type
            if (plugin.Host.config["grid_type"] != null)
                grid_type = int.Parse(plugin.Host.config["grid_type"]);

            // @eams add / area unit
            if (plugin.Host.config["area_unit"] != null)
                area_unit = double.Parse(plugin.Host.config["area_unit"]);
        }

        bool deleteLastLAND = false;    // @eams add
        bool first_validate = false;    // @eams add

        private void GridUI_Load(object sender, EventArgs e)
        {
            loading = true;
            if (!loadedfromfile)
                loadsettings();

            // setup state before settings load
            CHK_advanced_CheckedChanged(null, null);

            TRK_zoom.Value = (float)map.Zoom;

            label1.Text += " (" + CurrentState.DistanceUnit + ")";
            label24.Text += " (" + CurrentState.SpeedUnit + ")";

            loading = false;

            first_validate = true;
            domainUpDown1_ValueChanged(this, null);

            // @eams add
            int wpcount = plugin.Host.WPCount();
            if (wpcount > 0)
            {
                // end RTL/LAND delete
                plugin.Host.DeleteWP(wpcount - 1);
                CHK_toandland.Checked = false;
                deleteLastLAND = true;
            }
            else
            {
                CHK_toandland.Checked = true;
            }

//            BUT_Accept_Click(this, null);   // @eams add
        }

        private void GridUI_Resize(object sender, EventArgs e)
        {
            map.ZoomAndCenterMarkers("polygons");
        }

        // Load/Save
        public void LoadGrid()
        {
            System.Xml.Serialization.XmlSerializer reader = new System.Xml.Serialization.XmlSerializer(typeof(GridData));

            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "*.grid|*.grid";
                ofd.ShowDialog();

                if (File.Exists(ofd.FileName))
                {
                    using (StreamReader sr = new StreamReader(ofd.FileName))
                    {
                        var test = (GridData)reader.Deserialize(sr);

                        loading = true;
                        loadgriddata(test);
                        loading = false;
                    }
                }
            }
        }

        public void SaveGrid()
        {
            System.Xml.Serialization.XmlSerializer writer = new System.Xml.Serialization.XmlSerializer(typeof(GridData));

            var griddata = savegriddata();

            using (SaveFileDialog sfd = new SaveFileDialog())
            {
                sfd.Filter = "*.grid|*.grid";
                var result = sfd.ShowDialog();

                if (sfd.FileName != "" && result == DialogResult.OK)
                {
                    using (StreamWriter sw = new StreamWriter(sfd.FileName))
                    {
                        writer.Serialize(sw, griddata);
                    }
                }
            }
        }

        void loadgriddata(GridData griddata)
        {
            list = griddata.poly;

            CMB_camera.Text = griddata.camera;
            NUM_altitude.Value = griddata.alt;
            TXT_altitude.Text = Decimal.Round(NUM_altitude.Value, 1, MidpointRounding.AwayFromZero).ToString();   //@eams add
            NUM_angle.Value = griddata.angle;
            CHK_camdirection.Checked = griddata.camdir;
            CHK_usespeed.Checked = griddata.usespeed;
            NUM_UpDownFlySpeed.Value = griddata.speed;
            TXT_FlySpeed.Text = Decimal.Round(NUM_UpDownFlySpeed.Value, 1, MidpointRounding.AwayFromZero).ToString();   //@eams add
            CHK_toandland.Checked = griddata.autotakeoff;
            CHK_toandland_RTL.Checked = griddata.autotakeoff_RTL;
            NUM_split.Value = griddata.splitmission;


            NUM_Distance.Value = griddata.dist;
            TXT_Distance.Text = Decimal.Round(NUM_Distance.Value, 1, MidpointRounding.AwayFromZero).ToString();   //@eams add
            NUM_overshoot.Value = griddata.overshoot1;
            NUM_overshoot2.Value = griddata.overshoot2;
            NUM_leadin.Value = griddata.leadin;
            CMB_startfrom.Text = griddata.startfrom;
            num_overlap.Value = griddata.overlap;
            num_sidelap.Value = griddata.sidelap;
            NUM_spacing.Value = griddata.spacing;
            chk_crossgrid.Checked = griddata.crossgrid;

            rad_trigdist.Checked = griddata.trigdist;
            rad_digicam.Checked = griddata.digicam;
            rad_repeatservo.Checked = griddata.repeatservo;
            chk_stopstart.Checked = griddata.breaktrigdist;

            NUM_reptservo.Value = griddata.repeatservo_no;
            num_reptpwm.Value = griddata.repeatservo_pwm;
            NUM_repttime.Value = griddata.repeatservo_cycle;

            num_setservono.Value = griddata.setservo_no;
            num_setservolow.Value = griddata.setservo_low;
            num_setservohigh.Value = griddata.setservo_high;

            // Copter Settings
            NUM_copter_delay.Value = griddata.copter_delay;
            CHK_copter_headinghold.Checked = griddata.copter_headinghold_chk;
            //TXT_headinghold.Text = griddata.copter_headinghold.ToString();    //@eams disabled
            TXT_headinghold.Text = Decimal.Round(NUM_angle.Value, 0, MidpointRounding.AwayFromZero).ToString();   //@eams add
            TXT_angle.Text = Decimal.Round(NUM_angle.Value, 0, MidpointRounding.AwayFromZero).ToString();   //@eams add

            // Plane Settings
            NUM_Lane_Dist.Value = griddata.minlaneseparation;

            // update display options last
            CHK_internals.Checked = griddata.internals;
            CHK_footprints.Checked = griddata.footprints;
            CHK_advanced.Checked = griddata.advanced;

            loadedfromfile = true;
        }

        GridData savegriddata()
        {
            GridData griddata = new GridData();

            griddata.poly = list;

            griddata.camera = CMB_camera.Text;
            griddata.alt = NUM_altitude.Value;
            griddata.angle = NUM_angle.Value;
            griddata.camdir = CHK_camdirection.Checked;
            griddata.speed = NUM_UpDownFlySpeed.Value;
            griddata.usespeed = CHK_usespeed.Checked;
            griddata.autotakeoff = CHK_toandland.Checked;
            griddata.autotakeoff_RTL = CHK_toandland_RTL.Checked;
            griddata.splitmission = NUM_split.Value;

            griddata.internals = CHK_internals.Checked;
            griddata.footprints = CHK_footprints.Checked;
            griddata.advanced = CHK_advanced.Checked;

            griddata.dist = NUM_Distance.Value;
            griddata.overshoot1 = NUM_overshoot.Value;
            griddata.overshoot2 = NUM_overshoot2.Value;
            griddata.leadin = NUM_leadin.Value;
            griddata.startfrom = CMB_startfrom.Text;
            griddata.overlap = num_overlap.Value;
            griddata.sidelap = num_sidelap.Value;
            griddata.spacing = NUM_spacing.Value;
            griddata.crossgrid = chk_crossgrid.Checked;

            // Copter Settings
            griddata.copter_delay = NUM_copter_delay.Value;
            griddata.copter_headinghold_chk = CHK_copter_headinghold.Checked;
            griddata.copter_headinghold = decimal.Parse(TXT_headinghold.Text);

            // Plane Settings
            griddata.minlaneseparation = NUM_Lane_Dist.Value;

            griddata.trigdist = rad_trigdist.Checked;
            griddata.digicam = rad_digicam.Checked;
            griddata.repeatservo = rad_repeatservo.Checked;
            griddata.breaktrigdist = chk_stopstart.Checked;

            griddata.repeatservo_no = NUM_reptservo.Value;
            griddata.repeatservo_pwm = num_reptpwm.Value;
            griddata.repeatservo_cycle = NUM_repttime.Value;

            griddata.setservo_no = num_setservono.Value;
            griddata.setservo_low = num_setservolow.Value;
            griddata.setservo_high = num_setservohigh.Value;

            return griddata;
        }

        void loadsettings()
        {
            if (plugin.Host.config.ContainsKey("grid_camera"))
            {
                loadsetting("grid_alt", NUM_altitude);
                TXT_altitude.Text = Decimal.Round(NUM_altitude.Value, 1, MidpointRounding.AwayFromZero).ToString();   //@eams add
                loadsetting("grid_angle", NUM_angle);       // @eams enabled
                loadsetting("grid_camdir", CHK_camdirection);
                loadsetting("grid_usespeed", CHK_usespeed);
                loadsetting("grid_speed", NUM_UpDownFlySpeed);
                TXT_FlySpeed.Text = Decimal.Round(NUM_UpDownFlySpeed.Value, 1, MidpointRounding.AwayFromZero).ToString();   //@eams add
                loadsetting("grid_autotakeoff", CHK_toandland);
                loadsetting("grid_autotakeoff_RTL", CHK_toandland_RTL);

                loadsetting("grid_dist", NUM_Distance);
                TXT_Distance.Text = Decimal.Round(NUM_Distance.Value, 1, MidpointRounding.AwayFromZero).ToString();   //@eams add
                loadsetting("grid_overshoot1", NUM_overshoot);
                loadsetting("grid_overshoot2", NUM_overshoot2);
                loadsetting("grid_leadin", NUM_leadin);
                loadsetting("grid_startfrom", CMB_startfrom);
                loadsetting("grid_overlap", num_overlap);
                loadsetting("grid_sidelap", num_sidelap);
                loadsetting("grid_spacing", NUM_spacing);
                loadsetting("grid_crossgrid", chk_crossgrid);

                // Should probably be saved as one setting, and us logic
                loadsetting("grid_trigdist", rad_trigdist);
                loadsetting("grid_digicam", rad_digicam);
                loadsetting("grid_repeatservo", rad_repeatservo);
                loadsetting("grid_breakstopstart", chk_stopstart);

                loadsetting("grid_repeatservo_no", NUM_reptservo);
                loadsetting("grid_repeatservo_pwm", num_reptpwm);
                loadsetting("grid_repeatservo_cycle", NUM_repttime);

                // do set servo @eams add
                loadsetting("grid_dosetservo", rad_do_set_servo);
                loadsetting("grid_dosetservo_no", num_setservono);
                loadsetting("grid_dosetservo_PWML", num_setservolow);
                loadsetting("grid_dosetservo_PWMH", num_setservohigh);

                // camera last to it invokes a reload
                loadsetting("grid_camera", CMB_camera);

                // Copter Settings
                loadsetting("grid_copter_delay", NUM_copter_delay);
                loadsetting("grid_copter_headinghold_chk", CHK_copter_headinghold);   //@eams enabled
                TXT_headinghold.Text = Decimal.Round(NUM_angle.Value, 0, MidpointRounding.AwayFromZero).ToString();   //@eams add
                TXT_angle.Text = Decimal.Round(NUM_angle.Value, 0, MidpointRounding.AwayFromZero).ToString();   //@eams add
                loadsetting("grid_offset", TXT_offset); //@eams add

                // Plane Settings
                loadsetting("grid_min_lane_separation", NUM_Lane_Dist);

                loadsetting("grid_internals", CHK_internals);
                loadsetting("grid_footprints", CHK_footprints);
                loadsetting("grid_advanced", CHK_advanced);
            }
        }

        void loadsetting(string key, Control item)
        {
            // soft fail on bad param
            try
            {
                if (plugin.Host.config.ContainsKey(key))
                {
                    if (item is NumericUpDown)
                    {
                        ((NumericUpDown)item).Value = decimal.Parse(plugin.Host.config[key].ToString());
                    }
                    else if (item is ComboBox)
                    {
                        ((ComboBox)item).Text = plugin.Host.config[key].ToString();
                    }
                    else if (item is CheckBox)
                    {
                        ((CheckBox)item).Checked = bool.Parse(plugin.Host.config[key].ToString());
                    }
                    else if (item is RadioButton)
                    {
                        ((RadioButton)item).Checked = bool.Parse(plugin.Host.config[key].ToString());
                    }
                    else if (item is TextBox)   //@eams add
                    {
                        ((TextBox)item).Text = plugin.Host.config[key].ToString();
                    }
                }
            }
            catch { }
        }

        void savesettings()
        {
            plugin.Host.config["grid_camera"] = CMB_camera.Text;
            plugin.Host.config["grid_alt"] = NUM_altitude.Value.ToString();
            plugin.Host.config["grid_angle"] = NUM_angle.Value.ToString();
            plugin.Host.config["grid_camdir"] = CHK_camdirection.Checked.ToString();

            plugin.Host.config["grid_usespeed"] = CHK_usespeed.Checked.ToString();
            plugin.Host.config["grid_speed"] = NUM_UpDownFlySpeed.Value.ToString(); //@eams add

            plugin.Host.config["grid_dist"] = NUM_Distance.Value.ToString();
            plugin.Host.config["grid_overshoot1"] = NUM_overshoot.Value.ToString();
            plugin.Host.config["grid_overshoot2"] = NUM_overshoot2.Value.ToString();
            plugin.Host.config["grid_leadin"] = NUM_leadin.Value.ToString();
            plugin.Host.config["grid_overlap"] = num_overlap.Value.ToString();
            plugin.Host.config["grid_sidelap"] = num_sidelap.Value.ToString();
            plugin.Host.config["grid_spacing"] = NUM_spacing.Value.ToString();
            plugin.Host.config["grid_crossgrid"] = chk_crossgrid.Checked.ToString();

            plugin.Host.config["grid_startfrom"] = CMB_startfrom.Text;

            //            plugin.Host.config["grid_autotakeoff"] = CHK_toandland.Checked.ToString();    //@eams disabled
            plugin.Host.config["grid_autotakeoff_RTL"] = CHK_toandland_RTL.Checked.ToString();

            plugin.Host.config["grid_internals"] = CHK_internals.Checked.ToString();
            plugin.Host.config["grid_footprints"] = CHK_footprints.Checked.ToString();
            plugin.Host.config["grid_advanced"] = CHK_advanced.Checked.ToString();

            plugin.Host.config["grid_trigdist"] = rad_trigdist.Checked.ToString();
            plugin.Host.config["grid_digicam"] = rad_digicam.Checked.ToString();
            plugin.Host.config["grid_repeatservo"] = rad_repeatservo.Checked.ToString();
            plugin.Host.config["grid_breakstopstart"] = chk_stopstart.Checked.ToString();

            // do set servo @emas
            plugin.Host.config["grid_dosetservo"] = rad_do_set_servo.Checked.ToString();
            plugin.Host.config["grid_dosetservo_no"] = num_setservono.Value.ToString();
            plugin.Host.config["grid_dosetservo_PWML"] = num_setservolow.Value.ToString();
            plugin.Host.config["grid_dosetservo_PWMH"] = num_setservohigh.Value.ToString();

            // Copter Settings
            plugin.Host.config["grid_copter_delay"] = NUM_copter_delay.Value.ToString();
            plugin.Host.config["grid_copter_headinghold_chk"] = CHK_copter_headinghold.Checked.ToString();
            plugin.Host.config["grid_offset"] = TXT_offset.Text;    //@eams add

            // Plane Settings
            plugin.Host.config["grid_min_lane_separation"] = NUM_Lane_Dist.Value.ToString();
        }

        private void xmlcamera(bool write, string filename)
        {
            bool exists = File.Exists(filename);

            if (write || !exists)
            {
                try
                {
                    XmlTextWriter xmlwriter = new XmlTextWriter(filename, Encoding.ASCII);
                    xmlwriter.Formatting = Formatting.Indented;

                    xmlwriter.WriteStartDocument();

                    xmlwriter.WriteStartElement("Cameras");

                    foreach (string key in cameras.Keys)
                    {
                        try
                        {
                            if (key == "")
                                continue;
                            xmlwriter.WriteStartElement("Camera");
                            xmlwriter.WriteElementString("name", cameras[key].name);
                            xmlwriter.WriteElementString("flen", cameras[key].focallen.ToString(new System.Globalization.CultureInfo("en-US")));
                            xmlwriter.WriteElementString("imgh", cameras[key].imageheight.ToString(new System.Globalization.CultureInfo("en-US")));
                            xmlwriter.WriteElementString("imgw", cameras[key].imagewidth.ToString(new System.Globalization.CultureInfo("en-US")));
                            xmlwriter.WriteElementString("senh", cameras[key].sensorheight.ToString(new System.Globalization.CultureInfo("en-US")));
                            xmlwriter.WriteElementString("senw", cameras[key].sensorwidth.ToString(new System.Globalization.CultureInfo("en-US")));
                            xmlwriter.WriteEndElement();
                        }
                        catch { }
                    }

                    xmlwriter.WriteEndElement();

                    xmlwriter.WriteEndDocument();
                    xmlwriter.Close();

                }
                catch (Exception ex) { CustomMessageBox.Show(ex.ToString()); }
            }
            else
            {
                try
                {
                    using (XmlTextReader xmlreader = new XmlTextReader(filename))
                    {
                        while (xmlreader.Read())
                        {
                            xmlreader.MoveToElement();
                            try
                            {
                                switch (xmlreader.Name)
                                {
                                    case "Camera":
                                        {
                                            camerainfo camera = new camerainfo();

                                            while (xmlreader.Read())
                                            {
                                                bool dobreak = false;
                                                xmlreader.MoveToElement();
                                                switch (xmlreader.Name)
                                                {
                                                    case "name":
                                                        camera.name = xmlreader.ReadString();
                                                        break;
                                                    case "imgw":
                                                        camera.imagewidth = float.Parse(xmlreader.ReadString(), new System.Globalization.CultureInfo("en-US"));
                                                        break;
                                                    case "imgh":
                                                        camera.imageheight = float.Parse(xmlreader.ReadString(), new System.Globalization.CultureInfo("en-US"));
                                                        break;
                                                    case "senw":
                                                        camera.sensorwidth = float.Parse(xmlreader.ReadString(), new System.Globalization.CultureInfo("en-US"));
                                                        break;
                                                    case "senh":
                                                        camera.sensorheight = float.Parse(xmlreader.ReadString(), new System.Globalization.CultureInfo("en-US"));
                                                        break;
                                                    case "flen":
                                                        camera.focallen = float.Parse(xmlreader.ReadString(), new System.Globalization.CultureInfo("en-US"));
                                                        break;
                                                    case "Camera":
                                                        cameras[camera.name] = camera;
                                                        dobreak = true;
                                                        break;
                                                }
                                                if (dobreak)
                                                    break;
                                            }
                                            string temp = xmlreader.ReadString();
                                        }
                                        break;
                                    case "Config":
                                        break;
                                    case "xml":
                                        break;
                                    default:
                                        if (xmlreader.Name == "") // line feeds
                                            break;
                                        //config[xmlreader.Name] = xmlreader.ReadString();
                                        break;
                                }
                            }
                            catch (Exception ee) { Console.WriteLine(ee.Message); } // silent fail on bad entry
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine("Bad Camera File: " + ex.ToString()); } // bad config file

                // populate list
                foreach (var camera in cameras.Values)
                {
                    if (!CMB_camera.Items.Contains(camera.name))
                        CMB_camera.Items.Add(camera.name);
                }
            }
        }

        // Do Work
        //        private async void domainUpDown1_ValueChanged(object sender, EventArgs e)
        private void domainUpDown1_ValueChanged(object sender, EventArgs e)
        {
            if (loading)
                return;

            if (CMB_camera.Text != "")
            {
                doCalc();
            }

            // new grid system test

            if (chk_test.Checked)
            {
                grid = Utilities.Grid.CreateCorridor(list, CurrentState.fromDistDisplayUnit((double)NUM_altitude.Value),
                    (double)NUM_Distance.Value, (double)NUM_spacing.Value, (double)NUM_angle.Value,
                    (double)NUM_overshoot.Value, (double)NUM_overshoot2.Value,
                    (Utilities.Grid.StartPosition)Enum.Parse(typeof(Utilities.Grid.StartPosition), CMB_startfrom.Text), false,
                    (float)NUM_Lane_Dist.Value, (float)num_corridorwidth.Value, (float)NUM_leadin.Value);
            }
            else
            {
#if true
                double angle = (double)NUM_angle.Value;

                switch (grid_type)
                {
                    case 2:
                        grid = Utilities.Grid.CreateGrid2(list, CurrentState.fromDistDisplayUnit((double)NUM_altitude.Value),
                            (double)NUM_Distance.Value, (double)NUM_spacing.Value, ref angle,
                            (double)NUM_overshoot.Value, (double)NUM_overshoot2.Value,
                            (Utilities.Grid.StartPosition)Enum.Parse(typeof(Utilities.Grid.StartPosition), CMB_startfrom.Text), false,
                            (float)NUM_Lane_Dist.Value, (float)NUM_leadin.Value, MainV2.comPort.MAV.cs.HomeLocation, double.Parse(TXT_offset.Text), first_validate);
                        break;
                    case 3:
                        grid = Utilities.Grid.CreateGrid3(list, CurrentState.fromDistDisplayUnit((double)NUM_altitude.Value),
                            (double)NUM_Distance.Value, (double)NUM_spacing.Value, ref angle,
                            (double)NUM_overshoot.Value, (double)NUM_overshoot2.Value,
                            (Utilities.Grid.StartPosition)Enum.Parse(typeof(Utilities.Grid.StartPosition), CMB_startfrom.Text), false,
                            (float)NUM_Lane_Dist.Value, (float)NUM_leadin.Value, MainV2.comPort.MAV.cs.HomeLocation, double.Parse(TXT_offset.Text), first_validate);
                        break;
                    case 4:
                        grid = Utilities.Grid.CreateGrid4(list, CurrentState.fromDistDisplayUnit((double)NUM_altitude.Value),
                            (double)NUM_Distance.Value, (double)NUM_spacing.Value, ref angle,
                            (double)NUM_overshoot.Value, (double)NUM_overshoot2.Value,
                            (Utilities.Grid.StartPosition)Enum.Parse(typeof(Utilities.Grid.StartPosition), CMB_startfrom.Text), false,
                            (float)NUM_Lane_Dist.Value, (float)NUM_leadin.Value, MainV2.comPort.MAV.cs.HomeLocation, double.Parse(TXT_offset.Text), first_validate, area_unit);
                        break;
                    default:
                        grid = Utilities.Grid.CreateGrid(list, CurrentState.fromDistDisplayUnit((double)NUM_altitude.Value),
                            (double)NUM_Distance.Value, (double)NUM_spacing.Value, (double)NUM_angle.Value,
                            (double)NUM_overshoot.Value, (double)NUM_overshoot2.Value,
                            (Utilities.Grid.StartPosition)Enum.Parse(typeof(Utilities.Grid.StartPosition), CMB_startfrom.Text), false,
                            (float)NUM_Lane_Dist.Value, (float)NUM_leadin.Value, MainV2.comPort.MAV.cs.HomeLocation);
                        break;
                }

                NUM_angle.Value = (decimal)angle;
                TXT_headinghold.Text = (Math.Round(NUM_angle.Value)).ToString();
                TXT_angle.Text = (Math.Round(NUM_angle.Value)).ToString();
                first_validate = false;

#else
                grid = await Utilities.Grid.CreateGridAsync(list, CurrentState.fromDistDisplayUnit((double) NUM_altitude.Value),
                    (double) NUM_Distance.Value, (double) NUM_spacing.Value, (double) NUM_angle.Value,
                    (double) NUM_overshoot.Value, (double) NUM_overshoot2.Value,
                    (Utilities.Grid.StartPosition) Enum.Parse(typeof(Utilities.Grid.StartPosition), CMB_startfrom.Text), false,
                    (float) NUM_Lane_Dist.Value, (float) NUM_leadin.Value, MainV2.comPort.MAV.cs.HomeLocation);
#endif
            }

            map.HoldInvalidation = true;

            routesOverlay.Routes.Clear();
            routesOverlay.Polygons.Clear();
            routesOverlay.Markers.Clear();

            GMapMarkerOverlap.Clear();

            if (chk_crossgrid.Checked)
            {
                // add crossover
                Utilities.Grid.StartPointLatLngAlt = grid[grid.Count - 1];

                grid.AddRange(Utilities.Grid.CreateGrid(list, CurrentState.fromDistDisplayUnit((double)NUM_altitude.Value),
                    (double)NUM_Distance.Value, (double)NUM_spacing.Value, (double)NUM_angle.Value + 90.0,
                    (double)NUM_overshoot.Value, (double)NUM_overshoot2.Value,
                    Utilities.Grid.StartPosition.Point, false,
                    (float)NUM_Lane_Dist.Value, (float)NUM_leadin.Value, MainV2.comPort.MAV.cs.HomeLocation));
            }

            if (CHK_boundary.Checked)
                AddDrawPolygon();

            if (grid.Count == 0)
            {
                map.ZoomAndCenterMarkers("routes");
                return;
            }

            int strips = 0;
            int images = 0;
            int a = 1;
            PointLatLngAlt prevprevpoint = grid[0];
            PointLatLngAlt prevpoint = grid[0];
            // distance to/from home
            double routetotal = grid.First().GetDistance(MainV2.comPort.MAV.cs.HomeLocation) / 1000.0 +
                               grid.Last().GetDistance(MainV2.comPort.MAV.cs.HomeLocation) / 1000.0;
            List<PointLatLng> segment = new List<PointLatLng>();
            double maxgroundelevation = double.MinValue;
            double mingroundelevation = double.MaxValue;
            double startalt = plugin.Host.cs.HomeAlt;

            foreach (var item in grid)
            {
                double currentalt = srtm.getAltitude(item.Lat, item.Lng).alt;
                mingroundelevation = Math.Min(mingroundelevation, currentalt);
                maxgroundelevation = Math.Max(maxgroundelevation, currentalt);

                prevprevpoint = prevpoint;

                if (item.Tag == "M")
                {
                    images++;

                    if (CHK_internals.Checked)
                    {
                        routesOverlay.Markers.Add(new GMarkerGoogle(item, GMarkerGoogleType.green) { ToolTipText = a.ToString(), ToolTipMode = MarkerTooltipMode.OnMouseOver });
                        a++;

                        segment.Add(prevpoint);
                        segment.Add(item);
                        prevpoint = item;
                    }
                    try
                    {
                        if (TXT_fovH.Text != "")
                        {
                            if (CHK_footprints.Checked)
                            {
                                double fovh = double.Parse(TXT_fovH.Text);
                                double fovv = double.Parse(TXT_fovV.Text);

                                getFOV(item.Alt + startalt - currentalt, ref fovh, ref fovv);

                                double startangle = 0;

                                if (!CHK_camdirection.Checked)
                                {
                                    startangle += 90;
                                }

                                double angle1 = startangle - (Math.Sin((fovh / 2.0) / (fovv / 2.0)) * rad2deg);
                                double dist1 = Math.Sqrt(Math.Pow(fovh / 2.0, 2) + Math.Pow(fovv / 2.0, 2));

                                double bearing = (double)NUM_angle.Value;

                                if (CHK_copter_headinghold.Checked)
                                {
                                    bearing = Convert.ToInt32(TXT_headinghold.Text);
                                }

                                if (chk_test.Checked)
                                    bearing = prevprevpoint.GetBearing(item);

                                double fovha = 0;
                                double fovva = 0;
                                getFOVangle(ref fovha, ref fovva);
                                var itemcopy = new PointLatLngAlt(item);
                                itemcopy.Alt += startalt;
                                var temp = ImageProjection.calc(itemcopy, 0, 0, bearing + startangle, fovha, fovva);

                                List<PointLatLng> footprint = new List<PointLatLng>();
                                footprint.Add(temp[0]);
                                footprint.Add(temp[1]);
                                footprint.Add(temp[2]);
                                footprint.Add(temp[3]);

                                GMapPolygon poly = new GMapPolygon(footprint, a.ToString());
                                poly.Stroke =
                                    new Pen(Color.FromArgb(250 - ((a * 5) % 240), 250 - ((a * 3) % 240), 250 - ((a * 9) % 240)), 1);
                                poly.Fill = new SolidBrush(Color.Transparent);

                                GMapMarkerOverlap.Add(poly);

                                routesOverlay.Polygons.Add(poly);
                                a++;
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    if (item.Tag != "SM" && item.Tag != "ME")
                        strips++;

                    if (CHK_markers.Checked)
                    {
                        var marker = new GMapMarkerWP(item, a.ToString()) { ToolTipText = a.ToString(), ToolTipMode = MarkerTooltipMode.OnMouseOver };
                        routesOverlay.Markers.Add(marker);
                    }

                    segment.Add(prevpoint);
                    segment.Add(item);
                    prevpoint = item;
                    a++;
                }
                GMapRoute seg = new GMapRoute(segment, "segment" + a.ToString());
                seg.Stroke = new Pen(Color.Yellow, 4);
                seg.Stroke.DashStyle = System.Drawing.Drawing2D.DashStyle.Custom;
                seg.IsHitTestVisible = true;
                routetotal = routetotal + (float)seg.Distance;
                if (CHK_grid.Checked)
                {
                    routesOverlay.Routes.Add(seg);
                }
                else
                {
                    seg.Dispose();
                }

                segment.Clear();
            }

            if (CHK_footprints.Checked)
                routesOverlay.Markers.Add(GMapMarkerOverlap);
            /*      Old way of drawing route, incase something breaks using segments
            GMapRoute wproute = new GMapRoute(list2, "GridRoute");
            wproute.Stroke = new Pen(Color.Yellow, 4);
            if (chk_grid.Checked)
                routesOverlay.Routes.Add(wproute);
            */

            // turn radrad = tas^2 / (tan(angle) * G)
            float v_sq = (float)(((float)NUM_UpDownFlySpeed.Value / CurrentState.multiplierspeed) * ((float)NUM_UpDownFlySpeed.Value / CurrentState.multiplierspeed));
            float turnrad = (float)(v_sq / (float)(9.808f * Math.Tan(35 * deg2rad)));

            // Update Stats 
            if (DistUnits == "Feet")
            {
                // Area
                float area = (float)calcpolygonarea(list) * 10.7639f; // Calculate the area in square feet
                lbl_area.Text = area.ToString("#") + " ft^2";
                if (area < 21780f)
                {
                    lbl_area.Text = area.ToString("#") + " ft^2";
                }
                else
                {
                    area = area / 43560f;
                    if (area < 640f)
                    {
                        lbl_area.Text = area.ToString("0.##") + " acres";
                    }
                    else
                    {
                        area = area / 640f;
                        lbl_area.Text = area.ToString("0.##") + " miles^2";
                    }
                }

                // Distance
                double distance = routetotal * 3280.8399; // Calculate the distance in feet
                if (distance < 5280f)
                {
                    lbl_distance.Text = distance.ToString("#") + " ft";
                }
                else
                {
                    distance = distance / 5280f;
                    lbl_distance.Text = distance.ToString("0.##") + " miles";
                }

                lbl_spacing.Text = (NUM_spacing.Value * 3.2808399m).ToString("#.#") + " ft";
                lbl_grndres.Text = inchpixel;
                lbl_distbetweenlines.Text = (NUM_Distance.Value * 3.2808399m).ToString("0.##") + " ft";
                lbl_footprint.Text = feet_fovH + " x " + feet_fovV + " ft";
                lbl_turnrad.Text = (turnrad * 2 * 3.2808399).ToString("0") + " ft";
                lbl_gndelev.Text = (mingroundelevation * 3.2808399).ToString("0") + "-" + (maxgroundelevation * 3.2808399).ToString("0") + " ft";
            }
            else
            {
                // Meters
                lbl_area.Text = calcpolygonarea(list).ToString("#") + " m^2";
                lbl_distance.Text = routetotal.ToString("0.##") + " km";
                lbl_spacing.Text = NUM_spacing.Value.ToString("0.#") + " m";
                lbl_grndres.Text = TXT_cmpixel.Text;
                lbl_distbetweenlines.Text = NUM_Distance.Value.ToString("0.##") + " m";
                lbl_footprint.Text = TXT_fovH.Text + " x " + TXT_fovV.Text + " m";
                lbl_turnrad.Text = (turnrad * 2).ToString("0") + " m";
                lbl_gndelev.Text = mingroundelevation.ToString("0") + "-" + maxgroundelevation.ToString("0") + " m";

            }

            try
            {
                // speed m/s
                var speed = ((float)NUM_UpDownFlySpeed.Value / CurrentState.multiplierspeed);
                // cmpix cm/pixel
                var cmpix = float.Parse(TXT_cmpixel.Text.TrimEnd(new[] { 'c', 'm', ' ' }));
                // m pix = m/pixel
                var mpix = cmpix * 0.01;
                // gsd / 2.0
                var minmpix = mpix / 2.0;
                // min sutter speed
                var minshutter = speed / minmpix;
                lbl_minshutter.Text = "1/" + (minshutter - minshutter % 1).ToString();
            }
            catch { }

            double flyspeedms = CurrentState.fromSpeedDisplayUnit((double)NUM_UpDownFlySpeed.Value);

            lbl_pictures.Text = images.ToString();
            lbl_strips.Text = ((int)(strips / 2)).ToString();
            double seconds = ((routetotal * 1000.0) / ((flyspeedms) * 0.8));
            // reduce flying speed by 20 %
            lbl_flighttime.Text = secondsToNice(seconds);
            seconds = ((routetotal * 1000.0) / (flyspeedms));
            lbl_photoevery.Text = secondsToNice(((double)NUM_spacing.Value / flyspeedms));
            map.HoldInvalidation = false;
            if (!isMouseDown && sender != NUM_angle && sender != BUT_shiftup)    // @eams change
                map.ZoomAndCenterMarkers("routes");

//            CalcHeadingHold();    // @eams diabled

            map.Invalidate();
        }

        bool addwp_firsttime = true;
        private void AddWP(double Lng, double Lat, double Alt, object gridobject = null)
        {
            if (CHK_copter_headinghold.Checked)
            {
                plugin.Host.AddWPtoList(MAVLink.MAV_CMD.CONDITION_YAW, Convert.ToInt32(TXT_headinghold.Text), 0, 0, 0, 0, 0, 0, gridobject);
                if (grid_type == 3 || grid_type == 4)   // @eams add
                {
#if false
                    if (addwp_firsttime)
                    {
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DELAY, (float)3.0, 0, 0, 0, 0, 0, 0, gridobject);
                        addwp_firsttime = false;
                    }
                    else
                    {
#endif
                        //plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DELAY, (float)1.0, 0, 0, 0, 0, 0, 0, gridobject);
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.WAYPOINT, (float)1.0, 0, 0, 0, 0, 0, Alt, gridobject);
                    //}
                }
                else if (grid_type == 2)
                {
                    if (addwp_firsttime)
                    {
                        //plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DELAY, (float)1.0, 0, 0, 0, 0, 0, 0, gridobject);
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.WAYPOINT, (float)1.0, 0, 0, 0, 0, 0, Alt, gridobject);
                    }
                }
            }

            if (NUM_copter_delay.Value > 0)
            {
                if (grid_type == 2 && addwp_firsttime)
                {
                    plugin.Host.AddWPtoList(MAVLink.MAV_CMD.WAYPOINT, (float)3.0, 0, 0, 0, Lng, Lat, (double)(Alt * CurrentState.multiplierdist), gridobject);
                }
                else
                {
                    plugin.Host.AddWPtoList(MAVLink.MAV_CMD.WAYPOINT, (double)NUM_copter_delay.Value, 0, 0, 0, Lng, Lat, (double)(Alt * CurrentState.multiplierdist), gridobject);
                }
            }
            else
            {
                plugin.Host.AddWPtoList(MAVLink.MAV_CMD.WAYPOINT, 0, 0, 0, 0, Lng, Lat, (double)(Alt * CurrentState.multiplierdist), gridobject);
            }
            addwp_firsttime = false;
        }

        string secondsToNice(double seconds)
        {
            if (seconds < 0)
                return "Infinity Seconds";

            double secs = seconds % 60;
            int mins = (int)(seconds / 60) % 60;
            int hours = (int)(seconds / 3600);// % 24;

            if (hours > 0)
            {
                return hours + ":" + mins.ToString("00") + ":" + secs.ToString("00") + " Hours";
            }
            else if (mins > 0)
            {
                return mins + ":" + secs.ToString("00") + " Minutes";
            }
            else
            {
                return secs.ToString("0.00") + " Seconds";
            }
        }

        void AddDrawPolygon()
        {
            List<PointLatLng> list2 = new List<PointLatLng>();

            list.ForEach(x => { list2.Add(x); });

            var poly = new GMapPolygon(list2, "poly");
            poly.Stroke = new Pen(grid_color, 2);   // @eams change / original color is red
            poly.Fill = Brushes.Transparent;

            routesOverlay.Polygons.Add(poly);

            foreach (var item in list)
            {
                routesOverlay.Markers.Add(new GMarkerGoogle(item, GMarkerGoogleType.red));
            }
        }

        double calcpolygonarea(List<PointLatLngAlt> polygon)
        {
            // should be a closed polygon
            // coords are in lat long
            // need utm to calc area

            if (polygon.Count == 0)
            {
                CustomMessageBox.Show("Please define a polygon!");
                return 0;
            }

            // close the polygon
            if (polygon[0] != polygon[polygon.Count - 1])
                polygon.Add(polygon[0]); // make a full loop

            CoordinateTransformationFactory ctfac = new CoordinateTransformationFactory();

            IGeographicCoordinateSystem wgs84 = GeographicCoordinateSystem.WGS84;

            int utmzone = (int)((polygon[0].Lng - -186.0) / 6.0);

            IProjectedCoordinateSystem utm = ProjectedCoordinateSystem.WGS84_UTM(utmzone, polygon[0].Lat < 0 ? false : true);

            ICoordinateTransformation trans = ctfac.CreateFromCoordinateSystems(wgs84, utm);

            double prod1 = 0;
            double prod2 = 0;

            for (int a = 0; a < (polygon.Count - 1); a++)
            {
                double[] pll1 = { polygon[a].Lng, polygon[a].Lat };
                double[] pll2 = { polygon[a + 1].Lng, polygon[a + 1].Lat };

                double[] p1 = trans.MathTransform.Transform(pll1);
                double[] p2 = trans.MathTransform.Transform(pll2);

                prod1 += p1[0] * p2[1];
                prod2 += p1[1] * p2[0];
            }

            double answer = (prod1 - prod2) / 2;

            if (polygon[0] == polygon[polygon.Count - 1])
                polygon.RemoveAt(polygon.Count - 1); // unmake a full loop

            return Math.Abs(answer);
        }

        double getAngleOfLongestSide(List<PointLatLngAlt> list)
        {
            if (list.Count == 0)
                return 0;
            double angle = 0;
            double maxdist = 0;
            PointLatLngAlt last = list[list.Count - 1];
            foreach (var item in list)
            {
                if (item.GetDistance(last) > maxdist)
                {
                    angle = item.GetBearing(last);
                    maxdist = item.GetDistance(last);
                }
                last = item;
            }

            return (angle + 360) % 360;
        }

        void getFOV(double flyalt, ref double fovh, ref double fovv)
        {
            double focallen = (double)NUM_focallength.Value;
            double sensorwidth = double.Parse(TXT_senswidth.Text);
            double sensorheight = double.Parse(TXT_sensheight.Text);

            // scale      mm / mm
            double flscale = (1000 * flyalt) / focallen;

            //   mm * mm / 1000
            double viewwidth = (sensorwidth * flscale / 1000);
            double viewheight = (sensorheight * flscale / 1000);

            float fovh1 = (float)(Math.Atan(sensorwidth / (2 * focallen)) * rad2deg * 2);
            float fovv1 = (float)(Math.Atan(sensorheight / (2 * focallen)) * rad2deg * 2);

            fovh = viewwidth;
            fovv = viewheight;
        }

        void getFOVangle(ref double fovh, ref double fovv)
        {
            double focallen = (double)NUM_focallength.Value;
            double sensorwidth = double.Parse(TXT_senswidth.Text);
            double sensorheight = double.Parse(TXT_sensheight.Text);

            fovh = (float)(Math.Atan(sensorwidth / (2 * focallen)) * rad2deg * 2);
            fovv = (float)(Math.Atan(sensorheight / (2 * focallen)) * rad2deg * 2);
        }

        void doCalc()
        {
            try
            {
                // entered values
                float flyalt = (float)CurrentState.fromDistDisplayUnit((float)NUM_altitude.Value);
                int imagewidth = int.Parse(TXT_imgwidth.Text);
                int imageheight = int.Parse(TXT_imgheight.Text);

                int overlap = (int)num_overlap.Value;
                int sidelap = (int)num_sidelap.Value;

                double viewwidth = 0;
                double viewheight = 0;

                getFOV(flyalt, ref viewwidth, ref viewheight);

                TXT_fovH.Text = viewwidth.ToString("#.#");
                TXT_fovV.Text = viewheight.ToString("#.#");
                // Imperial
                feet_fovH = (viewwidth * 3.2808399f).ToString("#.#");
                feet_fovV = (viewheight * 3.2808399f).ToString("#.#");

                //    mm  / pixels * 100
                TXT_cmpixel.Text = ((viewheight / imageheight) * 100).ToString("0.00 cm");
                // Imperial
                inchpixel = (((viewheight / imageheight) * 100) * 0.393701).ToString("0.00 inches");

                NUM_spacing.ValueChanged -= domainUpDown1_ValueChanged;
                NUM_Distance.ValueChanged -= domainUpDown1_ValueChanged;

                if (CHK_camdirection.Checked)
                {
                    NUM_spacing.Value = (decimal)((1 - (overlap / 100.0f)) * viewheight);
                    //                    NUM_Distance.Value = (decimal)((1 - (sidelap / 100.0f)) * viewwidth);     // @eams diabled
                }
                else
                {
                    NUM_spacing.Value = (decimal)((1 - (overlap / 100.0f)) * viewwidth);
                    //                    NUM_Distance.Value = (decimal)((1 - (sidelap / 100.0f)) * viewheight);    // @eams disabled
                }
                NUM_spacing.ValueChanged += domainUpDown1_ValueChanged;
                NUM_Distance.ValueChanged += domainUpDown1_ValueChanged;
            }
            catch { return; }
        }

        private void CalcHeadingHold()
        {
            int previous = (int)Math.Round(Convert.ToDecimal(((UpDownBase)NUM_angle).Text)); //((UpDownBase)sender).Text
            int current = (int)Math.Round(NUM_angle.Value);

            int change = current - previous;

            if (change > 0) // Positive change
            {
                int val = Convert.ToInt32(TXT_headinghold.Text) + change;
                if (val > 359)
                {
                    val = val - 360;
                }
                TXT_headinghold.Text = val.ToString();
            }

            if (change < 0) // Negative change
            {
                int val = Convert.ToInt32(TXT_headinghold.Text) + change;
                if (val < 0)
                {
                    val = val + 360;
                }
                TXT_headinghold.Text = val.ToString();
            }
        }

        // Map Operators
        private void map_OnRouteEnter(GMapRoute item)
        {
            string dist;
            if (DistUnits == "Feet")
            {
                dist = ((float)item.Distance * 3280.84f).ToString("0.##") + " ft";
            }
            else
            {
                dist = ((float)item.Distance * 1000f).ToString("0.##") + " m";
            }
            if (marker != null)
            {
                if (routesOverlay.Markers.Contains(marker))
                    routesOverlay.Markers.Remove(marker);
            }

            PointLatLng point = currentMousePosition;

            marker = new GMapMarkerRect(point);
            marker.ToolTip = new GMapToolTip(marker);
            marker.ToolTipMode = MarkerTooltipMode.Always;
            marker.ToolTipText = "Line: " + dist;
            routesOverlay.Markers.Add(marker);
        }

        private void map_OnRouteLeave(GMapRoute item)
        {
            if (marker != null)
            {
                try
                {
                    if (routesOverlay.Markers.Contains(marker))
                        routesOverlay.Markers.Remove(marker);
                }
                catch { }
            }
        }

        private void map_OnMarkerLeave(GMapMarker item)
        {
            if (!isMouseDown)
            {
                if (item is GMapMarker)
                {
                    // when you click the context menu this triggers and causes problems
                    CurrentGMapMarker = null;
                }

            }
        }

        private void map_OnMarkerEnter(GMapMarker item)
        {
            if (!isMouseDown)
            {
                if (item is GMapMarker)
                {
                    CurrentGMapMarker = item as GMapMarker;
                    CurrentGMapMarkerStartPos = CurrentGMapMarker.Position;
                }
            }
        }

        private void map_MouseUp(object sender, MouseEventArgs e)
        {
            MouseDownEnd = map.FromLocalToLatLng(e.X, e.Y);

            // Console.WriteLine("MainMap MU");

            if (e.Button == MouseButtons.Right) // ignore right clicks
            {
                return;
            }

            if (isMouseDown) // mouse down on some other object and dragged to here.
            {
                if (e.Button == MouseButtons.Left)
                {
                    isMouseDown = false;
                }
                if (!isMouseDraging)
                {
                    if (CurrentGMapMarker != null)
                    {
                        // Redraw polygon
                        //AddDrawPolygon();
                    }
                }
            }
            isMouseDraging = false;
            CurrentGMapMarker = null;
            CurrentGMapMarkerIndex = 0;
            CurrentGMapMarkerStartPos = null;
        }

        private void map_MouseDown(object sender, MouseEventArgs e)
        {
            MouseDownStart = map.FromLocalToLatLng(e.X, e.Y);

            if (e.Button == MouseButtons.Left && Control.ModifierKeys != Keys.Alt)
            {
                isMouseDown = true;
                isMouseDraging = false;

                if (CurrentGMapMarkerStartPos != null)
                    CurrentGMapMarkerIndex = list.FindIndex(c => c.ToString() == CurrentGMapMarkerStartPos.ToString());
            }
        }

        private void map_MouseMove(object sender, MouseEventArgs e)
        {
            PointLatLng point = map.FromLocalToLatLng(e.X, e.Y);
            currentMousePosition = point;

            if (MouseDownStart == point)
                return;

            if (!isMouseDown)
            {
                // update mouse pos display
                //SetMouseDisplay(point.Lat, point.Lng, 0);
            }

            //draging
            if (e.Button == MouseButtons.Left && isMouseDown)
            {
                isMouseDraging = true;

                if (CurrentGMapMarker != null)
                {
                    if (CurrentGMapMarkerIndex == -1)
                    {
                        isMouseDraging = false;
                        return;
                    }

                    PointLatLng pnew = map.FromLocalToLatLng(e.X, e.Y);

                    CurrentGMapMarker.Position = pnew;

                    list[CurrentGMapMarkerIndex] = new PointLatLngAlt(pnew);
                    domainUpDown1_ValueChanged(sender, e);
                }
                else // left click pan
                {
                    double latdif = MouseDownStart.Lat - point.Lat;
                    double lngdif = MouseDownStart.Lng - point.Lng;

                    try
                    {
                        lock (thisLock)
                        {
                            map.Position = new PointLatLng(map.Position.Lat + latdif, map.Position.Lng + lngdif);
                        }
                    }
                    catch { }
                }
            }
        }

        private void map_OnMapZoomChanged()
        {
            if (map.Zoom > 0)
            {
                try
                {
                    TRK_zoom.Value = (float)map.Zoom;
                }
                catch { }
            }
        }

        // Operators
        private void trackBar1_ValueChanged(object sender, EventArgs e)
        {
            try
            {
                lock (thisLock)
                {
                    map.Zoom = TRK_zoom.Value;
                }
            }
            catch { }
        }

        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            try
            {
                lock (thisLock)
                {
                    map.Zoom = TRK_zoom.Value;
                }
            }
            catch { }
        }

        private void NUM_ValueChanged(object sender, EventArgs e)
        {
            domainUpDown1_ValueChanged(null, null);
        }

        private void CMB_camera_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (cameras.ContainsKey(CMB_camera.Text))
            {
                camerainfo camera = cameras[CMB_camera.Text];

                NUM_focallength.Value = (decimal)camera.focallen;
                TXT_imgheight.Text = camera.imageheight.ToString();
                TXT_imgwidth.Text = camera.imagewidth.ToString();
                TXT_sensheight.Text = camera.sensorheight.ToString();
                TXT_senswidth.Text = camera.sensorwidth.ToString();

                //NUM_Distance.Enabled = false;
            }

            GMapMarkerOverlap.Clear();

            domainUpDown1_ValueChanged(null, null);
        }

        private void TXT_TextChanged(object sender, EventArgs e)
        {
            domainUpDown1_ValueChanged(null, null);
        }

        private void CHK_camdirection_CheckedChanged(object sender, EventArgs e)
        {
            domainUpDown1_ValueChanged(null, null);
        }

        private void CHK_advanced_CheckedChanged(object sender, EventArgs e)
        {
            if (CHK_advanced.Checked)
            {
                if (!tabControl1.TabPages.Contains(tabGrid))
                    tabControl1.TabPages.Add(tabGrid);
                if (!tabControl1.TabPages.Contains(tabCamera))
                    tabControl1.TabPages.Add(tabCamera);
            }
            else
            {
                tabControl1.TabPages.Remove(tabGrid);
                tabControl1.TabPages.Remove(tabCamera);
            }
        }

        private void CHK_copter_headinghold_CheckedChanged(object sender, EventArgs e)
        {
            if (CHK_copter_headinghold.Checked)
            {
                TXT_headinghold.Enabled = true;
                CHK_copter_headingholdlock.Enabled = true;
                CHK_copter_headingholdlock.Checked = false;
                BUT_headingholdplus.Enabled = true;
                BUT_headingholdminus.Enabled = true;
            }
            else
            {
                TXT_headinghold.Enabled = false;
                CHK_copter_headingholdlock.Enabled = false;
                BUT_headingholdplus.Enabled = false;
                BUT_headingholdminus.Enabled = false;
            }
        }

        private void CHK_copter_headingholdlock_CheckedChanged(object sender, EventArgs e)
        {
            if (CHK_copter_headingholdlock.Checked)
            {
                TXT_headinghold.ReadOnly = false;
            }
            else
            {
                TXT_headinghold.ReadOnly = true;
                TXT_headinghold.Text = Decimal.Round(NUM_angle.Value).ToString();
            }
        }

        private void BUT_headingholdplus_Click(object sender, EventArgs e)
        {
            int previous = Convert.ToInt32(TXT_headinghold.Text);
            if (!CHK_copter_headingholdlock.Checked)
            {
                if (previous + 180 > 359)
                {
                    TXT_headinghold.Text = (previous - 180).ToString();
                }
                else
                {
                    TXT_headinghold.Text = (previous + 180).ToString();
                }
            }
            else
            {
                if (previous + 1 > 359)
                {
                    TXT_headinghold.Text = (previous - 359).ToString();
                }
                else
                {
                    TXT_headinghold.Text = (previous + 1).ToString();
                }
            }
        }

        private void BUT_headingholdminus_Click(object sender, EventArgs e)
        {
            int previous = Convert.ToInt32(TXT_headinghold.Text);

            if (!CHK_copter_headingholdlock.Checked)
            {
                if (previous - 180 < 0)
                {
                    TXT_headinghold.Text = (previous + 180).ToString();
                }
                else
                {
                    TXT_headinghold.Text = (previous - 180).ToString();
                }
            }
            else
            {
                if (previous - 1 < 0)
                {
                    TXT_headinghold.Text = (previous + 359).ToString();
                }
                else
                {
                    TXT_headinghold.Text = (previous - 1).ToString();
                }
            }
        }

        private void BUT_samplephoto_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog())
            {
                ofd.Filter = "*.jpg|*.jpg";

                ofd.ShowDialog();

                if (File.Exists(ofd.FileName))
                {
                    string fn = ofd.FileName;

                    Metadata lcMetadata = null;
                    try
                    {
                        FileInfo lcImgFile = new FileInfo(fn);
                        // Loading all meta data
                        lcMetadata = JpegMetadataReader.ReadMetadata(lcImgFile);

                        if (lcMetadata == null)
                            return;
                    }
                    catch (JpegProcessingException ex)
                    {
                        log.InfoFormat(ex.Message);
                        return;
                    }

                    foreach (AbstractDirectory lcDirectory in lcMetadata)
                    {
                        foreach (var tag in lcDirectory)
                        {
                            Console.WriteLine(lcDirectory.GetName() + " - " + tag.GetTagName() + " " + tag.GetTagValue().ToString());
                        }

                        if (lcDirectory.ContainsTag(ExifDirectory.TAG_EXIF_IMAGE_HEIGHT))
                        {
                            TXT_imgheight.Text = lcDirectory.GetInt(ExifDirectory.TAG_EXIF_IMAGE_HEIGHT).ToString();
                        }

                        if (lcDirectory.ContainsTag(ExifDirectory.TAG_EXIF_IMAGE_WIDTH))
                        {
                            TXT_imgwidth.Text = lcDirectory.GetInt(ExifDirectory.TAG_EXIF_IMAGE_WIDTH).ToString();
                        }

                        if (lcDirectory.ContainsTag(ExifDirectory.TAG_FOCAL_PLANE_X_RES))
                        {
                            var unit = lcDirectory.GetFloat(ExifDirectory.TAG_FOCAL_PLANE_UNIT);

                            // TXT_senswidth.Text = lcDirectory.GetDouble(ExifDirectory.TAG_FOCAL_PLANE_X_RES).ToString();
                        }

                        if (lcDirectory.ContainsTag(ExifDirectory.TAG_FOCAL_PLANE_Y_RES))
                        {
                            var unit = lcDirectory.GetFloat(ExifDirectory.TAG_FOCAL_PLANE_UNIT);

                            // TXT_sensheight.Text = lcDirectory.GetDouble(ExifDirectory.TAG_FOCAL_PLANE_Y_RES).ToString();
                        }

                        if (lcDirectory.ContainsTag(ExifDirectory.TAG_FOCAL_LENGTH))
                        {
                            try
                            {
                                var item = lcDirectory.GetFloat(ExifDirectory.TAG_FOCAL_LENGTH);
                                NUM_focallength.Value = (decimal)item;
                            }
                            catch { }
                        }


                        if (lcDirectory.ContainsTag(ExifDirectory.TAG_DATETIME_ORIGINAL))
                        {

                        }

                    }
                }
            }
        }

        private void BUT_save_Click(object sender, EventArgs e)
        {
            camerainfo camera = new camerainfo();

            string camname = "Default";

            if (MissionPlanner.Controls.InputBox.Show("Camera Name", "Please and a camera name", ref camname) != System.Windows.Forms.DialogResult.OK)
                return;

            CMB_camera.Text = camname;

            // check if camera exists alreay
            if (cameras.ContainsKey(CMB_camera.Text))
            {
                camera = cameras[CMB_camera.Text];
            }
            else
            {
                cameras.Add(CMB_camera.Text, camera);
            }

            try
            {
                camera.name = CMB_camera.Text;
                camera.focallen = (float)NUM_focallength.Value;
                camera.imageheight = float.Parse(TXT_imgheight.Text);
                camera.imagewidth = float.Parse(TXT_imgwidth.Text);
                camera.sensorheight = float.Parse(TXT_sensheight.Text);
                camera.sensorwidth = float.Parse(TXT_senswidth.Text);
            }
            catch { CustomMessageBox.Show("One of your entries is not a valid number"); return; }

            cameras[CMB_camera.Text] = camera;

            xmlcamera(true, Settings.GetUserDataDirectory() + "cameras.xml");
        }

        private void BUT_Accept_Click(object sender, EventArgs e)
        {
            addwp_firsttime = true;
            if (grid != null && grid.Count > 0)
            {
                MainV2.instance.FlightPlanner.quickadd = true;

                if (NUM_split.Value > 1 && CHK_toandland.Checked != true)
                {
                    CustomMessageBox.Show("You must use Land/RTL to split a mission", Strings.ERROR);
                    return;
                }

                var gridobject = savegriddata();

                int wpsplit = (int)Math.Round(grid.Count / NUM_split.Value, MidpointRounding.AwayFromZero);

                List<int> wpsplitstart = new List<int>();

                for (int splitno = 0; splitno < NUM_split.Value; splitno++)
                {
                    int wpstart = wpsplit * splitno;
                    int wpend = wpsplit * (splitno + 1);

                    while (wpstart != 0 && wpstart < grid.Count && grid[wpstart].Tag != "E")
                    {
                        wpstart--;
                    }

                    while (wpend > 0 && wpend < grid.Count && grid[wpend].Tag != "S")
                    {
                        wpend--;
                    }

                    if (CHK_toandland.Checked)
                    {
                        if (plugin.Host.cs.firmware == Firmwares.ArduCopter2)
                        {
#if true    // @eams change
                            var wpno = plugin.Host.AddWPtoList(MAVLink.MAV_CMD.TAKEOFF, 20, 0, 0, 0, 0, 0,
                                Decimal.ToDouble(NUM_altitude.Value), gridobject);
#else
                            var wpno = plugin.Host.AddWPtoList(MAVLink.MAV_CMD.TAKEOFF, 20, 0, 0, 0, 0, 0,
                                (int) (30*CurrentState.multiplierdist), gridobject);
#endif
                            wpsplitstart.Add(wpno);
                        }
                        else
                        {
                            var wpno = plugin.Host.AddWPtoList(MAVLink.MAV_CMD.TAKEOFF, 20, 0, 0, 0, 0, 0,
                                (int)(30 * CurrentState.multiplierdist), gridobject);

                            wpsplitstart.Add(wpno);
                        }
#if false
                        // @eams add set SERVO7_FUNCTION auto
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_PARAMETER,
                            703, MainV2.servo7_func_auto, 0, 0, 0, 0, 0, gridobject);
#endif
                        // @eams add
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_SERVO,
                            (float)num_setservono.Value,
                            (float)num_setservohigh.Value, 0, 0, 0, 0, 0,
                            gridobject);
                    }

                    if (CHK_usespeed.Checked)
                    {
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_CHANGE_SPEED, 0,
                        (double)((float)NUM_UpDownFlySpeed.Value / CurrentState.multiplierspeed), 0, 0, 0, 0, 0,
                            gridobject);
                    }

                    // @eams add startup delay
                    int grid_startup_delay = Settings.Instance.GetInt32("grid_startup_delay");
                    if (grid_startup_delay > 0)
                    {
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DELAY,
                            (float)grid_startup_delay, 0, 0, 0, 0, 0, 0,
                            gridobject);

                    }

                    int i = 0;
                    bool startedtrigdist = false;
                    PointLatLngAlt lastplla = PointLatLngAlt.Zero;
                    foreach (var plla in grid)
                    {
                        // skip before start point
                        if (i < wpstart)
                        {
                            i++;
                            continue;
                        }
                        // skip after endpoint
                        if (i >= wpend)
                            break;
                        if (i > wpstart)
                        {
                            // internal point check
                            if (plla.Tag == "M")
                            {
                                if (rad_repeatservo.Checked)
                                {
                                    if (!chk_stopstart.Checked)
                                    {
                                        AddWP(plla.Lng, plla.Lat, plla.Alt);
                                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_REPEAT_SERVO,
                                            (float)NUM_reptservo.Value,
                                            (float)num_reptpwm.Value, 1, (float)NUM_repttime.Value, 0, 0, 0,
                                            gridobject);
                                    }
                                }
                                if (rad_digicam.Checked)
                                {
                                    AddWP(plla.Lng, plla.Lat, plla.Alt);
                                    plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_DIGICAM_CONTROL, 1, 0, 0, 0, 0, 1, 0,
                                        gridobject);
                                }
                                if (grid_type == 3 || grid_type == 4)
                                {
                                    AddWP(plla.Lng, plla.Lat, plla.Alt);
                                    // open
                                    plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_SERVO,
                                        (float)num_setservono.Value,
                                        (float)num_setservolow.Value, 0, 0, 0, 0, 0,
                                        gridobject);

                                    // turn
                                    plugin.Host.AddWPtoList(MAVLink.MAV_CMD.LOITER_TURNS,
                                        (float)1,
                                        0, 0, 0, (double)plla.Lng, (double)plla.Lat, plla.Alt,
                                        gridobject);

                                    // close
                                    plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_SERVO,
                                        (float)num_setservono.Value,
                                        (float)num_setservohigh.Value, 0, 0, 0, 0, 0,
                                        gridobject);
                                }
                            }
                            else
                            {
                                // only add points that are ends
                                if (plla.Tag == "S" || plla.Tag == "E")
                                {
                                    if (plla.Lat != lastplla.Lat || plla.Lng != lastplla.Lng ||
                                        plla.Alt != lastplla.Alt)
                                        AddWP(plla.Lng, plla.Lat, plla.Alt);
                                }

                                // check trigger method
                                if (rad_trigdist.Checked)
                                {
                                    // if stopstart enabled, add wp and trigger start/stop
                                    if (chk_stopstart.Checked)
                                    {
                                        if (plla.Tag == "SM")
                                        {
                                            //  s > sm, need to dup check
                                            if (plla.Lat != lastplla.Lat || plla.Lng != lastplla.Lng ||
                                                plla.Alt != lastplla.Alt)
                                                AddWP(plla.Lng, plla.Lat, plla.Alt);

                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST,
                                                (float)NUM_spacing.Value,
                                                0, 0, 0, 0, 0, 0, gridobject);
                                        }
                                        else if (plla.Tag == "ME")
                                        {
                                            AddWP(plla.Lng, plla.Lat, plla.Alt);

                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, 0,
                                                0, 0, 0, 0, 0, 0, gridobject);
                                        }
                                    }
                                    else
                                    {
                                        // add single start trigger
                                        if (!startedtrigdist)
                                        {
                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST,
                                                (float)NUM_spacing.Value,
                                                0, 0, 0, 0, 0, 0, gridobject);
                                            startedtrigdist = true;
                                        }
                                        else if (plla.Tag == "ME")
                                        {
                                            AddWP(plla.Lng, plla.Lat, plla.Alt);
                                        }
                                    }
                                }
                                else if (rad_repeatservo.Checked)
                                {
                                    if (chk_stopstart.Checked)
                                    {
                                        if (plla.Tag == "SM")
                                        {
                                            if (plla.Lat != lastplla.Lat || plla.Lng != lastplla.Lng ||
                                                plla.Alt != lastplla.Alt)
                                                AddWP(plla.Lng, plla.Lat, plla.Alt);

                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_REPEAT_SERVO,
                                                (float)NUM_reptservo.Value,
                                                (float)num_reptpwm.Value, 999, (float)NUM_repttime.Value, 0, 0, 0,
                                                gridobject);
                                        }
                                        else if (plla.Tag == "ME")
                                        {
                                            AddWP(plla.Lng, plla.Lat, plla.Alt);

                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_REPEAT_SERVO,
                                                (float)NUM_reptservo.Value,
                                                (float)num_reptpwm.Value, 0, (float)NUM_repttime.Value, 0, 0, 0,
                                                gridobject);
                                        }
                                    }
                                }
                                else if (rad_do_set_servo.Checked)
                                {
                                    if (plla.Tag == "SM")
                                    {
                                        if (plla.Lat != lastplla.Lat || plla.Lng != lastplla.Lng ||
                                            plla.Alt != lastplla.Alt)
                                            AddWP(plla.Lng, plla.Lat, plla.Alt);

                                        // open
                                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_SERVO,
                                            (float)num_setservono.Value,
                                            (float)num_setservolow.Value, 0, 0, 0, 0, 0,
                                            gridobject);

                                        if (grid_type == 3 || grid_type == 4)
                                        {
                                            // turn
                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.LOITER_TURNS,
                                                (float)1,
                                                0, 0, 0, (double)plla.Lng, (double)plla.Lat, plla.Alt,
                                                gridobject);

                                            // close
                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_SERVO,
                                                (float)num_setservono.Value,
                                                (float)num_setservohigh.Value, 0, 0, 0, 0, 0,
                                                gridobject);
                                        }
                                    }
                                    else if (plla.Tag == "ME")
                                    {
                                        AddWP(plla.Lng, plla.Lat, plla.Alt);

                                        if (grid_type == 3 || grid_type == 4)
                                        {
                                            // open
                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_SERVO,
                                                (float)num_setservono.Value,
                                                (float)num_setservolow.Value, 0, 0, 0, 0, 0,
                                                gridobject);

                                            // turn
                                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.LOITER_TURNS,
                                                (float)1,
                                                0, 0, 0, (double)plla.Lng, (double)plla.Lat, plla.Alt,
                                                gridobject);

                                        }
                                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_SERVO,
                                            (float)num_setservono.Value,
                                            (float)num_setservohigh.Value, 0, 0, 0, 0, 0,
                                            gridobject);
                                    }
                                }
                            }
                        }
                        else
                        {
                            AddWP(plla.Lng, plla.Lat, plla.Alt, gridobject);
                        }
                        lastplla = plla;
                        ++i;
                    }

                    // end
#if false
                    // @eams add set SERVO7_FUNCTION normal
                    plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_PARAMETER,
                        703, MainV2.servo7_func_normal, 0, 0, 0, 0, 0, gridobject);
#endif
                    if (rad_trigdist.Checked)
                    {
                        plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_SET_CAM_TRIGG_DIST, 0, 0, 0, 0, 0, 0, 0, gridobject);
                    }

                    if (CHK_usespeed.Checked)
                    {
                        if (MainV2.comPort.MAV.param["WPNAV_SPEED"] != null)
                        {
                            double speed = MainV2.comPort.MAV.param["WPNAV_SPEED"].Value;
                            speed = speed / 100;
                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.DO_CHANGE_SPEED, 0, speed, 0, 0, 0, 0, 0, gridobject);
                        }
                    }

                    if (CHK_toandland.Checked)
                    {
                        if (CHK_toandland_RTL.Checked)
                        {
                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.RETURN_TO_LAUNCH, 0, 0, 0, 0, 0, 0, 0, gridobject);
                        }
                        else
                        {
#if true
                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.LOITER_UNLIM, 0, 0, 0, 0, 0, 0, 0, gridobject);
#else
                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.LAND, 0, 0, 0, 0, plugin.Host.cs.HomeLocation.Lng,
                                plugin.Host.cs.HomeLocation.Lat, 0, gridobject);
#endif
                        }
                    }

                    // @eams add
                    if (deleteLastLAND)
                    {
                        if (CHK_toandland_RTL.Checked)
                        {
                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.RETURN_TO_LAUNCH, 0, 0, 0, 0, 0, 0, 0, gridobject);
                        }
                        else
                        {
#if true
                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.LOITER_UNLIM, 0, 0, 0, 0, 0, 0, 0, gridobject);
#else
                            plugin.Host.AddWPtoList(MAVLink.MAV_CMD.LAND, 0, 0, 0, 0, plugin.Host.cs.HomeLocation.Lng,
                                plugin.Host.cs.HomeLocation.Lat, 0, gridobject);
#endif
                        }
                    }
                }

                if (NUM_split.Value > 1)
                {
                    int index = 0;
                    foreach (var i in wpsplitstart)
                    {
                        // add do jump
                        plugin.Host.InsertWP(index, MAVLink.MAV_CMD.DO_JUMP, i + wpsplitstart.Count + 1, 1, 0, 0, 0, 0, 0, gridobject);
                        index++;
                    }

                }

                // Redraw the polygon in FP
                plugin.Host.RedrawFPPolygon(list);

                // save camera fov's for use with footprints
                double fovha = 0;
                double fovva = 0;
                try
                {
                    getFOVangle(ref fovha, ref fovva);

                    if (CHK_camdirection.Checked)
                    {
                        Settings.Instance["camera_fovh"] = fovha.ToString();
                        Settings.Instance["camera_fovv"] = fovva.ToString();
                    }
                    else
                    {
                        Settings.Instance["camera_fovh"] = fovva.ToString();
                        Settings.Instance["camera_fovv"] = fovha.ToString();
                    }
                }
                catch (Exception ex)
                {
                    log.Error(ex);
                }

                savesettings();

                MainV2.instance.FlightPlanner.quickadd = false;

                MainV2.instance.FlightPlanner.writeKML();

                this.Close();
            }
            else
            {
                CustomMessageBox.Show("Bad Grid", "Error");
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.O))
            {
                LoadGrid();

                return true;
            }
            if (keyData == (Keys.Control | Keys.S))
            {
                SaveGrid();

                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void NUM_Lane_Dist_ValueChanged(object sender, EventArgs e)
        {
            // doCalc
            domainUpDown1_ValueChanged(sender, e);
        }

        // @eams add / below functions
#if false
        private void BUT_angleplus_Click(object sender, EventArgs e)
        {
            int previous = (int)NUM_angle.Value;

            if (previous + 1 > 359)
            {
//                TXT_headinghold.Text = (previous - 359).ToString();
                TXT_angle.Text = (previous - 359).ToString();
                NUM_angle.Value = (previous - 359);
            }
            else
            {
//                TXT_headinghold.Text = (previous + 1).ToString();
                TXT_angle.Text = (previous + 1).ToString();
                NUM_angle.Value = (previous + 1);
            }
        }

        private void BUT_angleminus_Click(object sender, EventArgs e)
        {
            int previous = (int)NUM_angle.Value;

            if (previous - 1 < 0)
            {
//                TXT_headinghold.Text = (previous + 359).ToString();
                TXT_angle.Text = (previous + 359).ToString();
                NUM_angle.Value = (previous + 359);
            }
            else
            {
//                TXT_headinghold.Text = (previous - 1).ToString();
                TXT_angle.Text = (previous - 1).ToString();
                NUM_angle.Value = (previous - 1);
            }
        }
#endif
        private double incrementValue = 0;
        private static int def_interval = 800;
        TextBox target = null;

        public double CurrentValue
        {
            set
            {
                if (target == null)
                {
                    return;
                }
                if (target == TXT_angle)
                {
                    if (value > 359)
                    {
                        value = 0;
                    }
                    if (value < 0)
                    {
                        value = 359;
                    }
                    TXT_angle.Text = ((int)value).ToString();
//                    TXT_headinghold.Text = value.ToString();
                    NUM_angle.Value = (decimal)value;
                }
                else
                {
                    if (value < 0.0)
                    {
                        value = 0.0;
                    }
                    target.Text = value.ToString("f1");
                    if (target == TXT_altitude)
                    {
                        NUM_altitude.Value = Convert.ToDecimal(value);
                    }
                    else if (target == TXT_FlySpeed)
                    {
                        NUM_UpDownFlySpeed.Value = Convert.ToDecimal(value);
                    }
                    else if (target == TXT_Distance)
                    {
                        NUM_Distance.Value = Convert.ToDecimal(value);
                    }
                }
            }
            get
            {
                double ret;
                double.TryParse(target.Text, out ret);
                return ret;
            }
        }

        int counter = 0;
        private void timer1_Tick(object sender, EventArgs e)
        {
            if (incrementValue == 0)
            {
                counter = 0;
                timer1.Stop();
                return;
            }
            counter++;
            if (counter < 10)
            {
                timer1.Interval = 200;
            }
            else
            {
                counter = 10;
                timer1.Interval = 55;
            }
//            CurrentValueAngle += incrementValue;
            CurrentValue += incrementValue;

        }

        private void BUT_angle_Down(object sender, MouseEventArgs e)
        {
            target = TXT_angle;
            CurrentValue += (sender == BUT_angleplus) ? 1 : -1;
            timer1.Interval = def_interval;
            incrementValue = (sender == BUT_angleplus) ? 1 : -1;
            timer1.Start();
        }

        private void BUT_angle_Up(object sender, MouseEventArgs e)
        {
            incrementValue = 0;
            target = null;
        }

        private void BUT_Close_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void BUT_zoomIn_Click(object sender, EventArgs e)
        {
            try
            {
                map.Zoom += 0.5;
                //                TRK_zoom.Value += (float)0.5;
#if false
                if (TRK_zoom.Maximum < TRK_zoom.Value)
                {
                    TRK_zoom.Value = TRK_zoom.Maximum;
                }
#endif
            }
            catch
            {
            }
        }

        private void BUT_zoomOut_Click(object sender, EventArgs e)
        {
            try
            {
                map.Zoom -= 0.5;
                //                TRK_zoom.Value -= (float)0.5;
#if false
                if (TRK_zoom.Maximum < TRK_zoom.Value)
                {
                    TRK_zoom.Value = TRK_zoom.Maximum;
                }
#endif
            }
            catch
            {
            }
        }
#if false
        private void BUT_offsetplus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_offset.Text, out value);
            value += 0.1;
            TXT_offset.Text = value.ToString();
    }

    private void BUT_offsetminus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_offset.Text, out value);
            value -= 0.1;
            if (value < 0.0)
            {
                value = 0.0;
            }
            TXT_offset.Text = value.ToString();
        }
#endif

        private void BUT_offset_MouseDown(object sender, MouseEventArgs e)
        {
            target = TXT_offset;
            CurrentValue += (sender == BUT_offsetplus) ? 0.1 : -0.1;
            timer1.Interval = def_interval;
            incrementValue = (sender == BUT_offsetplus) ? 0.1 : -0.1;
            timer1.Start();
        }

        private void BUT_offset_MouseUp(object sender, MouseEventArgs e)
        {
            incrementValue = 0;
            target = null;
        }
#if false
        private void BUT_altplus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_altitude.Text, out value);
            value += 0.1;
            TXT_altitude.Text = value.ToString("f1");
            NUM_altitude.Value = (Decimal)value;
        }

        private void BUT_altminus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_altitude.Text, out value);
            value -= 0.1;
            if (value < 0)
            {
                value = 0;
            }
            TXT_altitude.Text = value.ToString("f1");
            NUM_altitude.Value = (Decimal)value;
        }
#endif
        private void BUT_alt_MouseDown(object sender, MouseEventArgs e)
        {
            target = TXT_altitude;
            CurrentValue += (sender == BUT_altplus) ? 0.1 : -0.1;
            timer1.Interval = def_interval;
            incrementValue = (sender == BUT_altplus) ? 0.1 : -0.1;
            timer1.Start();
        }

        private void BUT_alt_MouseUp(object sender, MouseEventArgs e)
        {
            incrementValue = 0;
            target = null;
        }
#if false
        private void BUT_speedplus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_FlySpeed.Text, out value);
            value += 0.1;
            TXT_FlySpeed.Text = value.ToString();
            NUM_UpDownFlySpeed.Value = (Decimal)value;
        }

        private void BUT_speedminus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_FlySpeed.Text, out value);
            value -= 0.1;
            if (value < 0.0)
            {
                value = 0.0;
            }
            TXT_FlySpeed.Text = value.ToString();
            NUM_UpDownFlySpeed.Value = (Decimal)value;
        }
#endif
        private void BUT_speed_MouseDown(object sender, MouseEventArgs e)
        {
            target = TXT_FlySpeed;
            CurrentValue += (sender == BUT_speedplus) ? 0.1 : -0.1;
            timer1.Interval = def_interval;
            incrementValue = (sender == BUT_speedplus) ? 0.1 : -0.1;
            timer1.Start();
        }

        private void BUT_speed_MouseUp(object sender, MouseEventArgs e)
        {
            incrementValue = 0;
            target = null;
        }
#if false
        private void BUT_distplus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_Distance.Text, out value);
            value += 0.1;
            TXT_Distance.Text = value.ToString();
            NUM_Distance.Value = (Decimal)value;
        }

        private void BUT_distminus_Click(object sender, EventArgs e)
        {
            double value;
            double.TryParse(TXT_Distance.Text, out value);
            value -= 0.1;
            if (value < 0.0)
            {
                value = 0.0;
            }
            TXT_Distance.Text = value.ToString();
            NUM_Distance.Value = (Decimal)value;
        }
#endif
        private void BUT_dist_MouseDown(object sender, MouseEventArgs e)
        {
            target = TXT_Distance;
            CurrentValue += (sender == BUT_distplus) ? 0.1 : -0.1;
            timer1.Interval = def_interval;
            incrementValue = (sender == BUT_distplus) ? 0.1 : -0.1;
            timer1.Start();
        }

        private void BUT_dist_MouseUp(object sender, MouseEventArgs e)
        {
            incrementValue = 0;
            target = null;
        }

        private void grid_shift(int angle)
        {
            List<PointLatLngAlt> newgrid = new List<PointLatLngAlt>();
            foreach (var point in list)
            {
                newgrid.Add(point.newpos(angle, 0.5));
            }
            list.Clear();
            foreach (var point in newgrid)
            {
                list.Add(point);
            }
            domainUpDown1_ValueChanged(BUT_shiftup, null);
        }

        private void BUT_shiftup_Click(object sender, EventArgs e)
        {
            grid_shift(0);
        }

        private void BUT_shiftright_Click(object sender, EventArgs e)
        {
            grid_shift(90);
        }

        private void BUT_shiftdown_Click(object sender, EventArgs e)
        {
            grid_shift(180);
        }

        private void BUT_shiftleft_Click(object sender, EventArgs e)
        {
            grid_shift(270);
        }
    }
}