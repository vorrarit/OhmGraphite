using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;
using OpenHardwareMonitor.Hardware;
using Timer = System.Timers.Timer;
using System.Configuration.Install;

namespace OhmGraphite
{
	class Program
	{
		static void Main(string[] args)
		{
			// Run as windows service if we're not in a terminal
			if (!Environment.UserInteractive)
			{
				ServiceBase.Run(new OhmGraphite());
			}
			else
			{
				OhmGraphite.RunDaemon();
			}
		}
	}

	// Derive from `ServiceBase` so that this program can be a console app as well as a
	// windows service.
	public class OhmGraphite : ServiceBase
	{
		// token that'll be used to receive a stop event from windows services
		private static readonly CancellationTokenSource Source = new CancellationTokenSource();

		// Create a looping timer that triggers an event every 5 seconds
		private static Timer _timer = new Timer(interval: 5 * 1000) { AutoReset = true };

		public static void RunDaemon()
		{
			// We'll want to capture all available hardware metrics
			// to send to graphite
			var computer = new Computer
			{
				GPUEnabled = true,
				MainboardEnabled = true,
				CPUEnabled = true,
				RAMEnabled = true,
				FanControllerEnabled = true,
				HDDEnabled = true
			};

			// Aside: wish that the API supported `using`
			computer.Open();

			try
			{
				// Hardcode TCP connection to our local graphite server
				using (var client = new TcpClient("192.168.1.20", 2003))
				using (var networkStream = client.GetStream())
				using (var writer = new StreamWriter(networkStream))
				{
					// Start timing
					_timer.Enabled = true;

					// Since this block will never finish normal execution, the writer will
					// not be disposed under normal circumstances, so it is ok to capture the
					// writer for our event (this would normally be bad practice because the
					// event could be using a disposed writer)
					_timer.Elapsed += (sender, eventArgs) => CaptureMetrics(computer, writer);

					while (!Source.IsCancellationRequested)
					{
						// 2147483647ms < 25 days, and since systems can be online longer than
						// 25 days, loop forever, sleeping 25 days at a time. No need to
						// check for return value because the while loop checks if a
						// cancellation has occurred
						Source.Token.WaitHandle.WaitOne(millisecondsTimeout: int.MaxValue);
					}

					// Set timer to null, which will de-register all events so that the
					// elapsed event doesn't have access to a disposed writer
					_timer = null;
				}
			}
			finally
			{
				// When this application has been interrupted or computer shutdown, clean
				// up all resources from the usings and the computer
				computer.Close();
			}
		}

		private static void CaptureMetrics(IComputer computer, TextWriter writer)
		{
			// Grab unix timestamp at the start of the update so that all metrics
			// are reported at the same time.
			var time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
			foreach (var hardware in computer.Hardware)
			{
				hardware.Update();
				foreach (var hardwareSensor in hardware.Sensors)
				{
					// Take the sensor's identifier (eg. /nvidiagpu/0/load/0)
					// and tranform into nvidiagpu.0.load.<name> where <name>
					// is the name of the sensor lowercased with spaces removed.
					// A name like "GPU Core" is turned into "gpucore". Also
					// since some names are like "cpucore#2", turn them into
					// separate metrics by replacing "#" with "."
					var name = hardwareSensor.Identifier.ToString()
						.Replace('/', '.')
						.Substring(1);
					name = name.Remove(name.LastIndexOf('.'));
					name += '.' + hardwareSensor.Name.ToLower()
								.Replace(" ", null).Replace('#', '.');

					// Graphite API wants <metric> <value> <timestamp>. We prefix the metric
					// with `ohm` as to not overwrite potentially existing metrics
					writer.WriteLine($"ohm.{name} {hardwareSensor.Value ?? 0.0} {time:d}");

					Console.WriteLine($"ohm.{name} {hardwareSensor.Value ?? 0.0} {time:d}");
				}
			}

			// Output current to time to stdout to track progress
			Console.Out.WriteLine($"{DateTimeOffset.Now:s}");
		}

		protected override void OnStart(string[] args) => RunDaemon();
		protected override void OnStop() => Source.Cancel();
	}

	[RunInstaller(true)]
	public class OhmGraphiteInstaller : Installer
	{
		public OhmGraphiteInstaller()
		{
			// Instantiate installers for process and services.
			var processInstaller = new ServiceProcessInstaller
			{
				Account = ServiceAccount.LocalSystem
			};

			var serviceInstaller1 = new ServiceInstaller
			{
				StartType = ServiceStartMode.Automatic,
				ServiceName = "ohm-graphite"
			};

			Installers.Add(serviceInstaller1);
			Installers.Add(processInstaller);
		}
	}
}
