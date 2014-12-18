using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading;
using System.Diagnostics;
using Clarion;
using Clarion.Framework;
using Clarion.Framework.Extensions;
using Clarion.Framework.Templates;


namespace Dota2Shopper
{
	class MainClass
	{
		static State MyState = null;
		static DotaAgent[] DotaAgents = null;

		static public void RunReplay() {
			// run each agent through its associated hero's gameplay
			foreach (DotaAgent dotaAgent in DotaAgents) {
				Console.WriteLine(dotaAgent.MyHero.Name);
				dotaAgent.Run();
				Console.WriteLine("\n");
			}
		}

		static public void RunSimulation() {
			World.LoggingLevel = TraceLevel.Off;

			Console.WriteLine("Running the Dota 2 shopping task...");

			InputLoop();
		}

		private static int InputLoop() {
			char input = 'y';
			bool runAgain = true;
			LearningModeValues LearningMode = LearningModeValues.Imitative;

			while (runAgain == true) {
				// give user a chance to change the learning mode of the agents
				// bottom-level heavy, top-level heavy, imitative using fixed rules
				Console.Write("Select learning mode (b/t/i): ");
				input = Console.ReadKey().KeyChar;
				Console.WriteLine("");
				if (input == 'b' || input == 'B') {
					LearningMode = LearningModeValues.BottomLayer;
				}
				else if (input == 't' || input == 'T') {
					LearningMode = LearningModeValues.TopLayer;
				}
				else if (input == 'i' || input == 'I') {
					LearningMode = LearningModeValues.Imitative;
				}
					
				foreach (DotaAgent agent in DotaAgents) {
					agent.LearningMode = LearningMode;
				}

				for (int i = 0; i < 10; i++) {
					Console.WriteLine("Trial #" + i + ": ");
					RunTask(50);
				}

				Console.Write("Continue? (y/n): ");
				input = Console.ReadKey().KeyChar;
				Console.WriteLine("");

				if (input == 'y' || input == 'Y') {
					// reset the task
				}
				else {
					runAgain = false;
				}
			}
/*
			// give user a chance to write the current state of each agent out
			// to file to be reloaded later
			Console.Write("Save agent states to file? (y/n): ");
			input = Console.ReadKey().KeyChar;
			Console.WriteLine("");
			if (input == 'y' || input == 'Y') {
				Console.Write("Enter directory name: ");
				String directory = Console.ReadLine();
				Console.WriteLine("");

				WriteAgentsToXML(directory);
			}
*/
			return 0;
		}

		private static int RunTask(int numTrials)
		{
			int choicesMade = 0;
			int correctCounter = 0;

			for (int i = 0; i < numTrials; i++) {
				int tick = 0;

				for (int a = 0; a < 10; a++) {
					DotaAgents[a].ResetState();
				}

				while (tick <= MyState.GetLastTick()) {
					for (int a = 0; a < 1; a++) {
						DotaAgents[a].RunTaskStep(tick, ref choicesMade, ref correctCounter);
					}
					for (int a = 0; a < 1; a++) {
						DotaAgents[a].UpdateState(tick, false);
					}

					tick++;
				}

				int progress = (int)(((double)(i+1) / (double)numTrials) * 100);
				Console.CursorLeft = 0;
				Console.Write(progress + "% Complete...");
			}

			int correctPercentage = (int)((float)correctCounter / (float)choicesMade * 100.0);
			Console.WriteLine(correctPercentage + "% correct.");

			return 0;
		}
/*
		public static void WriteAgentsToXML(String directory) {
			if (directory.Length > 0) {
				directory = "agents" + Path.DirectorySeparatorChar + directory + Path.DirectorySeparatorChar;
			}

			foreach (DotaAgent dotaAgent in DotaAgents) {
				dotaAgent.WriteAgentToXML(directory);
			}
		}
*/
		public static void Main(string[] args) {
			char input;

/*
			String directory = null;
			Console.Write("Load agents from file? (y/n): ");
			input = Console.ReadKey().KeyChar;
			Console.WriteLine("");
			if (input == 'y' || input == 'Y') {
				Console.Write("Enter directory name: ");
				directory = Console.ReadLine();
				Console.WriteLine("");
				if (directory.Length == 0) {
					directory = "agents" + Path.DirectorySeparatorChar;
				}
				else {
					directory = "agents" + Path.DirectorySeparatorChar + directory + Path.DirectorySeparatorChar;
				}
			}
*/
			// read in the parsed log file
			Console.Write("Reading in the log file...");
			MyState = new State();
			MyState.Init("log.xml");
			Console.WriteLine("Done.");

			// for each Hero character in the match, create an associated agent
			Console.Write("Initializing agents...");
			DotaAgents = new DotaAgent[10];
			int i = 0;
			foreach (Hero hero in MyState.GetRadiantTeam()) {
				DotaAgents[i] = new DotaAgent();
				DotaAgents[i].MyHero = hero;
				DotaAgents[i].Init();
//				DotaAgents[i].Init(directory);
				i++;
			}
			foreach (Hero hero in MyState.GetDireTeam()) {
				DotaAgents[i] = new DotaAgent();
				DotaAgents[i].MyHero = hero;
				DotaAgents[i].Init();
//				DotaAgents[i].Init(directory);
				i++;
			}
			Console.WriteLine("Done.");

//			RunReplay();
			RunSimulation();


			// kill the agents to end the task
			Console.Write("Killing the agents to end the program...");
			for (i = 0; i < 10; i++) {
				DotaAgents[i].KillAgent();
			}
			Console.WriteLine("Done.");
			Console.WriteLine("The Dota 2 shopping task is finished.");
		}

	}
}
