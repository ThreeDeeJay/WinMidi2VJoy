// Quick-and-dirty MIDI-to-VJoy converter by Åke Wallebom - Handles multiple MIDI devices and (should handle) multiple vjoy joysticks

using System;
using System.Collections.Generic;

using Sanford.Multimedia.Midi;
using vJoyInterfaceWrap;

namespace WinMidi2VJoy
{
	class JoyTarget
	{
		public uint JoyId { get; private set; }
		public uint Btn { get; private set; }
		public HID_USAGES Axis { get; private set; }

		public JoyTarget(uint joyId, uint btn)
		{
			JoyId = joyId;
			Btn = btn;
		}
		public JoyTarget(uint joyId, HID_USAGES axis)
		{
			JoyId = joyId;
			Axis = axis;
		}
	}
	class Mapping
	{
		public IEnumerable<uint> JoyIds { get { return joyIds; } }

		ICollection<uint> joyIds = new HashSet<uint>();
		IDictionary<Tuple<int, int>, JoyTarget> midiToTarget = new Dictionary<Tuple<int,int>, JoyTarget>();

		public void AddBtnMapping(int channel, int data1, uint joyId, uint btn)
		{
			joyIds.Add(joyId);
			midiToTarget.Add(new Tuple<int, int>(channel, data1), new JoyTarget(joyId, btn));
		}
		public void AddAxisMapping(int channel, int data1, uint joyId, HID_USAGES axis)
		{
			joyIds.Add(joyId);
			midiToTarget.Add(new Tuple<int, int>(channel, data1), new JoyTarget(joyId, axis));
		}

		public JoyTarget GetJoyTarget(int channel, int data1)
		{
			midiToTarget.TryGetValue(new Tuple<int, int>(channel, data1), out var joyTarget);
			return joyTarget;
		}
	}

	class Program
	{
		static Mapping stat_mapping;
		static vJoy stat_vJoy;
		const int MaxMidi = 127;
		const int MaxAxis = 32767;
		const float MidiToAxisFactor = (float)MaxAxis / MaxMidi;

		static void Main(string[] args)
		{
			Console.WriteLine("MIDI-to-VJoy converter by Åke Wallebom");
			Console.Error.WriteLine("Button Mapping: <midi channel>,<data1 min>-<data1 max>,<joyid>,<buttonoffset> or" + Environment.NewLine + "Axis Mapping: <midi channel>,<data1>,<joyid>,<axis (x,y,z,rx,ry,rz,sl0,sl1)>" + Environment.NewLine + "E.g. 0,23-31,1,-22 0,33-41,1,-22 0,14,1,x 0,15,1,y 0,16,1,z 0,17,1,rx 0,18,1,ry 0,19,1,rz 0,20,1,sl0 0,21,1,sl1" + Environment.NewLine);

			stat_mapping = new Mapping();

			// e.g. command-line:
			// 0,23-31,1,-22 0,33-41,1,-22 0,14,1,x 0,15,1,y 0,16,1,z
			if (!ParseMappings(args, stat_mapping))
			{
				Console.Error.WriteLine("Command-line argument invalid");
				return;
			}

			stat_vJoy = InitVJoy();
			if (stat_vJoy == null)
			{
				Console.Error.WriteLine("Couldn't init vjoy driver");
				return;
			}

			foreach (var joyId in stat_mapping.JoyIds)
			{
				if (!InitVJoyById(stat_vJoy, joyId))
				{
					Console.Error.WriteLine("Couldn't init vjoy id = " + joyId);
					return;
				}
			}

			StartMidi();
		}

		private static void StartMidi()
		{
			int deviceCount = InputDevice.DeviceCount;
			for (int i = 0; i < deviceCount; i++)
			{
				var deviceCap = InputDevice.GetDeviceCapabilities(i);
				Console.WriteLine("Using MIDI Device: " + deviceCap.name);
				var input = new InputDevice(i);
				input.ChannelMessageReceived += Input_ChannelMessageReceived;
				input.StartRecording();
			}
		}

		private static bool ParseMappings(string[] args, Mapping mapping)
		{
			foreach (var arg in args)
			{
				// <channel>,<rangemin>-<rangemax>,<joyid>,<buttonoffset>
				string[] parts = arg.Split(',');
				if (parts.Length != 4)
					return false;

				if (!int.TryParse(parts[0], out var channel))
					return false;
				if (!uint.TryParse(parts[2], out var joyId))
					return false;

				// <rangemin>-<rangemax>
				int rangeMin, rangeMax;
				string[] rangeParts = parts[1].Split('-');
				if (rangeParts.Length == 2)
				{
					if (!int.TryParse(rangeParts[0], out rangeMin) || !int.TryParse(rangeParts[1], out rangeMax))
						return false;

					if (rangeMax < rangeMin)
						return false;
				}
				else
				{
					if (!int.TryParse(rangeParts[0], out rangeMin))
						return false;
					rangeMax = rangeMin;
				}

				if (int.TryParse(parts[3], out var buttonOffset))
				{
					// Add mappings generated from range definition
					for (int i = rangeMin; i <= rangeMax; i++)
					{
						var btn = (uint)(i + buttonOffset);
						Console.WriteLine("Mapping: MIDI Channel " + channel + ", Data1 = " + i + " to vJoy " + joyId + " button " + btn);
						mapping.AddBtnMapping(channel, i, joyId, btn);
					}
				}
				else
				{
					if (rangeMin != rangeMax)
					{
						Console.Error.WriteLine("Input range not allowed for axis-mapping");
					}
					if (!Enum.TryParse<HID_USAGES>("HID_USAGE_" + parts[3].ToUpperInvariant(), out var axis))
					{
						Console.Error.WriteLine("Invalid axis specified: " + parts[3]);
						return false;
					}

					Console.WriteLine("Mapping: MIDI Channel " + channel + ", Data1 = " + rangeMin + " to vJoy " + joyId + " axis " + axis);
					mapping.AddAxisMapping(channel, rangeMin, joyId, axis);
				}
			}

			return true;
		}

		private static void Input_ChannelMessageReceived(object sender, ChannelMessageEventArgs e)
		{
			Console.WriteLine("MIDI Input, Channel: " + e.Message.MidiChannel + " Data1: " + e.Message.Data1 + " Data2: " + e.Message.Data2);

			var target = stat_mapping.GetJoyTarget(e.Message.MidiChannel, e.Message.Data1);
			if (target != null)
			{
				if (target.Btn != 0)
				{
					bool state = e.Message.Data2 != 0;
					Console.WriteLine("VJoy " + target.JoyId + ", set button " + target.Btn + " = " + state);
					stat_vJoy.SetBtn(state, target.JoyId, target.Btn);
				}
				else // axis
				{
					int hidValue = (int)Math.Round(e.Message.Data2 * MidiToAxisFactor);
					Console.WriteLine("VJoy " + target.JoyId + ", set axis " + target.Axis + " = " + hidValue);
					stat_vJoy.SetAxis(hidValue, target.JoyId, target.Axis);
				}
			}
		}

		private static vJoy InitVJoy()
		{
			var vJoy = new vJoy();
			if (!vJoy.vJoyEnabled())
			{
				Console.WriteLine("vJoy driver not enabled");
				return null;
			}

			// Check if dll matches the driver version
			uint verDll = 0, verDrv = 0;
			bool match = vJoy.DriverMatch(ref verDll, ref verDrv);
			if (match)
			{
				Console.WriteLine("Driver and DLL are same version ({0:X})", verDll);
			}
			else
			{
				Console.WriteLine("Driver ({0:X}) and DLL ({1:x}) version mismatch (could work anyway)", verDrv, verDll);
			}

			return vJoy;
		}

		private static bool InitVJoyById(vJoy vJoy, uint id)
		{ 
			// Get the state of the requested device
			VjdStat status = vJoy.GetVJDStatus(id);
			switch (status)
			{
				case VjdStat.VJD_STAT_OWN:
				case VjdStat.VJD_STAT_FREE:
					break;
				case VjdStat.VJD_STAT_BUSY:
					Console.Error.WriteLine("vJoy {0} busy", id);
					return false;
				case VjdStat.VJD_STAT_MISS:
					Console.Error.WriteLine("vJoy {0} missing", id);
					return false;
				default:
					Console.Error.WriteLine("vJoy {0} error {1}", id, status);
					return false;
			}

			// Acquire joystick 
			if (!vJoy.AcquireVJD(id))
			{
				Console.Error.WriteLine("Failed to acquire vJoy {0}", id);
				return false;
			}
			else
			{
				Console.WriteLine("Acquired: vJoy {0}", id);
			}

			// Reset this joy
			vJoy.ResetVJD(id);

			return true;
		}
	}
}
