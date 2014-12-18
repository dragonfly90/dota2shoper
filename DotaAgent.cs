using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Formatters.Binary;
using System.Xml.Serialization;
using Clarion;
using Clarion.Framework;
using Clarion.Framework.Extensions;
using Clarion.Framework.Templates;
using Clarion.Plugins;

namespace Dota2Shopper
{
	// BottomLayer - emphasize using the bottom layer to choose actions (~exploratory)
	// TopLayer - emphasize using previously extracted rules to choose actions (~mature)
	// Imitative - use fixed rules to select the same action the human player chose (~training)
	public enum LearningModeValues {BottomLayer, TopLayer, Imitative};

	// used to specify how the DotaAgent should initialize its Clarion agent
	// Create - create a new agent from scratch
	// FromFile - attempt to load in an existing agent from file, if this fails, default to creating a new agent
	public enum InitModeValues {Create, FromFile};


	public class DotaAgent
	{

		public Hero MyHero {get; set;}
		public Agent MyAgent;
		private LearningModeValues learningMode;


		int Level = 1;	// the hero's level
		int Health = 2;	// 0 = low; 1 = mid; 2 = high
		int Mana = 2; 	// 0 = low; 1 = mid; 2 = high

		// create an array for counting the number of each type of item currently in the hero's inventory
		// base the size of the array on the total number of entries in the ItemId enum list
		// Note: the enum list also includes an entry for "empty", which is, of course, not an item
		//	but could be used as a quick way to tell how much free space is left
		//	though this is complicated by the nature of the inventory / stash system
		static int ItemCount = Enum.GetNames(typeof(ItemId)).Length;
		int[] Items = new int[ItemCount];


		// static Clarion variables

		// possible input values to the Clarion model
		static DimensionValuePair[] LevelInputs;
		static DimensionValuePair[] HealthInputs;
		static DimensionValuePair[] ManaInputs;
		static DimensionValuePair[,] InventoryInputs;

		// possible outputs from the Clarion model
		static ExternalActionChunk[] PurchaseActions;

		static ExternalActionChunk PlayerChoice;


		// initialize all the input and output parameters for the Clarion agent(s)
		static DotaAgent() {
			LevelInputs = new DimensionValuePair[25];
			for (int i = 0; i < 25; i++) {
				LevelInputs[i] = World.NewDimensionValuePair("Level", (i+1).ToString());
			}

			HealthInputs = new DimensionValuePair[3];
			HealthInputs[0] = World.NewDimensionValuePair("health", "low");
			HealthInputs[1] = World.NewDimensionValuePair("health", "mid");
			HealthInputs[2] = World.NewDimensionValuePair("health", "high");

			ManaInputs = new DimensionValuePair[3];
			ManaInputs[0] = World.NewDimensionValuePair("mana", "low");
			ManaInputs[1] = World.NewDimensionValuePair("mana", "mid");
			ManaInputs[2] = World.NewDimensionValuePair("mana", "high");

			InventoryInputs = new DimensionValuePair[ItemCount, 4];
			PurchaseActions = new ExternalActionChunk[ItemCount];
			for (int i = 0; i < ItemCount; i++) {
				for (int j = 0; j < 4; j++) {
					InventoryInputs[i,j] = World.NewDimensionValuePair(((ItemId)i).ToString(), j.ToString());
				}

				PurchaseActions[i] = World.NewExternalActionChunk(((ItemId)i).ToString());
			}
		}

		public DotaAgent() {
			LearningMode = LearningModeValues.BottomLayer;

			MyHero = null;

			for (int i = 0; i < Items.Length; i++) {
				Items[i] = 0;
			}
		}

		// reset the state between trials
		// should not reset the Clarion agent's state
		// but do reset the associated hero and clear out the Dota state variables (e.g., inventory, level, etc.)
		public void ResetState() {
			MyHero.InitHero();
			MyHero.InitReplay();
			for (int i = 0; i < Items.Length; i++) {
				Items[i] = 0;
			}
		}

		public void Init(String directory) {
			if (directory == null) {
				Init();
				return;
			}

			try {
				Stream inStream = File.Open(directory + MyHero.Name + ".bin", FileMode.Open);
				BinaryFormatter bFormatter = new BinaryFormatter();
				MyAgent = (Agent)bFormatter.Deserialize(inStream);
				inStream.Close();
			}
			catch (Exception e) {
				Init();
			}
		}

		public void Init() {
			MyAgent = World.NewAgent(MyHero.Name);

			// set up the network and learning parameters for the agent
			SimplifiedQBPNetwork net = AgentInitializer.InitializeImplicitDecisionNetwork(MyAgent, SimplifiedQBPNetwork.Factory);

			net = AgentInitializer.InitializeImplicitDecisionNetwork(MyAgent, SimplifiedQBPNetwork.Factory);

			for (int i = 0; i < HealthInputs.GetLength(0); i++) {
//				net.Input.Add(HealthInputs[i]);
//				net.Input.Add(ManaInputs[i]);
			}
			for (int i = 0; i < InventoryInputs.GetLength(0); i++) {
				for (int j = 0; j < InventoryInputs.GetLength(1); j++) {
					net.Input.Add(InventoryInputs[i,j]);
				}

				net.Output.Add(PurchaseActions[i]);
			}

			FixedRule fr;
			for (int i = 0; i < PurchaseActions.Length; i++) {
				fr = AgentInitializer.InitializeActionRule(MyAgent, FixedRule.Factory, PurchaseActions[i], ImitativeSupportDelegate);

//				fr.GeneralizedCondition.Add(Inputs[i], true);

				MyAgent.Commit(fr);
			}

//			AddRules();

			//net.Parameters.LEARNING_RATE = 0.5;
			net.Parameters.LEARNING_RATE = 2.0;
//			net.Parameters.MOMENTUM = 0.02;

			MyAgent.Commit(net);


			MyAgent.ACS.Parameters.SELECTION_TEMPERATURE = 0.05;
			MyAgent.ACS.Parameters.DELETION_FREQUENCY = 100;

			MyAgent.ACS.Parameters.LEVEL_SELECTION_METHOD = ActionCenteredSubsystem.LevelSelectionMethods.COMBINED;
			MyAgent.ACS.Parameters.LEVEL_SELECTION_OPTION = ActionCenteredSubsystem.LevelSelectionOptions.FIXED;


			// set up the probabilities used to select which system will be chosen to select an action
			// at each step (should total 1.0):
			//	BL - bottom layer (reinforcement learning neural net)
			//	RER - rule extraction and refinement - extracts rules from the bottom layer
			//	IRL - independent rule learning - does not use the bottom layer for learning rules
			// 	FR - fixed rules - Clarion cannot change these (though they can be added/removed externally)
			// We are currently using fixed rules when we want the agent to immitate the human player and train
			//	the bottom layer
			MyAgent.ACS.Parameters.FIXED_BL_LEVEL_SELECTION_MEASURE = 0.33;
			MyAgent.ACS.Parameters.FIXED_RER_LEVEL_SELECTION_MEASURE = 0.33;
			MyAgent.ACS.Parameters.FIXED_IRL_LEVEL_SELECTION_MEASURE = 0;
			MyAgent.ACS.Parameters.FIXED_FR_LEVEL_SELECTION_MEASURE = 0.33;
			/*
			MyAgent.ACS.Parameters.FIXED_BL_LEVEL_SELECTION_MEASURE = 0.75;
			MyAgent.ACS.Parameters.FIXED_RER_LEVEL_SELECTION_MEASURE = 0.25;
			MyAgent.ACS.Parameters.FIXED_IRL_LEVEL_SELECTION_MEASURE = 0;
			MyAgent.ACS.Parameters.FIXED_FR_LEVEL_SELECTION_MEASURE = 0;
			*/


			//MyAgent.ACS.Parameters.VARIABLE_BL_BETA = 0.5;
			//MyAgent.ACS.Parameters.VARIABLE_RER_BETA = 0.5;
			//MyAgent.ACS.Parameters.VARIABLE_IRL_BETA = 0;
			//MyAgent.ACS.Parameters.VARIABLE_FR_BETA = 0;
		}

		// create simple rules to guide initial behavior
		// based on item recommendations from the in-game store GUI,
		// guidelines from strategy guides, etc.
		// Independent Rule Learning (IRL) rules are not fixed,
		//	and may be refined by the agent's learning mechanisms
/*		private void AddRules() {
			// set up a refinable action rule for each recommended item
			IRLRule itemRule = null;

			foreach (ItemId item in MyHero.RecommendedItemTable[(int)ItemRecommendations.Starting]) {
				itemRule = AgentInitializer.InitializeActionRule(MyAgent, IRLRule.Factory, PurchaseActions[(int)item], RecommendedItemPurchaseSupportDelegate);

				MyAgent.Commit(itemRule);
			}
		}
*/
		// calculate the support for a recommended item purchase action
		public double ComputeRecommendedItemPurchaseSupport(ActivationCollection currentInput, Clarion.Framework.Core.Rule r) {
			return 1;
		}

		// C# delegate that just passes the ComputeRecommendedItemPurchaseSupport method above
		public SupportCalculator RecommendedItemPurchaseSupportDelegate {
			get {return ComputeRecommendedItemPurchaseSupport;}
		}

		// getter / setter for the learning mode used by the Clarion agent
		// favor bottom layer, favor top layer, or use rules to implement imitative learning
		public LearningModeValues LearningMode {
			get {
				return learningMode;
			}
			set {
				learningMode = value;
	
				if (MyAgent == null) return;

				if (value == LearningModeValues.BottomLayer) {
					MyAgent.ACS.Parameters.FIXED_BL_LEVEL_SELECTION_MEASURE = 1.0;
					MyAgent.ACS.Parameters.FIXED_RER_LEVEL_SELECTION_MEASURE = 0.0;
					MyAgent.ACS.Parameters.FIXED_IRL_LEVEL_SELECTION_MEASURE = 0.0;
					MyAgent.ACS.Parameters.FIXED_FR_LEVEL_SELECTION_MEASURE = 0.0;
				}
				else if (value == LearningModeValues.TopLayer) {
					MyAgent.ACS.Parameters.FIXED_BL_LEVEL_SELECTION_MEASURE = 0.0;
					MyAgent.ACS.Parameters.FIXED_RER_LEVEL_SELECTION_MEASURE = 1.0;
					MyAgent.ACS.Parameters.FIXED_IRL_LEVEL_SELECTION_MEASURE = 0.0;
					MyAgent.ACS.Parameters.FIXED_FR_LEVEL_SELECTION_MEASURE = 0.0;
				}
				else if (value == LearningModeValues.Imitative) {
					MyAgent.ACS.Parameters.FIXED_BL_LEVEL_SELECTION_MEASURE = 1.0;
					MyAgent.ACS.Parameters.FIXED_RER_LEVEL_SELECTION_MEASURE = 0.0;
					MyAgent.ACS.Parameters.FIXED_IRL_LEVEL_SELECTION_MEASURE = 0.0;
					MyAgent.ACS.Parameters.FIXED_FR_LEVEL_SELECTION_MEASURE = 1.0;
				}
			}
		}

		public void UpdateState(int tick, bool verbose) {
			// check that we have a valid enumerator
			// also check that the time of the hero's next state update matches the current tick
			//	otherwise, there's nothing for us to do here until the simulation advances further
			if (MyHero.IsEnumeratorValid() == false || MyHero.GetCurrentStep().Tick > tick) {
				return;
			}
				

			// ok, so checking for the case where an item is just shuffled to a different slot in
			// 	the inventory is a bit messy
			// for each time step keep track of all the items modified
			// 	and keep a running total of increments and decrements
			//	we'll use that at the end to determine what has actually been purchased (if anything)
			//	at this time step
			// this should be able to handle the case where an item (or 2) are just swapped
			// 	from one slot to another
			// i.e., we'll decrement its count, but also increment it
			//	so there should be no net change once we're finished processing the time step
			// also the case where an inventory update was issued, but only the item's charges changed
			//	 - again no net change

			List<int> diffIds = new List<int>();
			List<int> diffCounts = new List<int>();
			int index = -1;	// the index of an item of interest and its count in the above lists

			List<Item> purchasedItems = new List<Item>();

			// check each state change in the time step
			foreach (StateChange diff in MyHero.GetCurrentStep().Diffs) {
				// for now, we are only interested in state changes that involve inventory updates and purchases

				if (diff.Type == UpdateType.ItemPurchase) {
					purchasedItems.Add(((ItemPurchase)diff).NewItem);
				}
				else if (diff.Type == UpdateType.InventoryUpdate) {
					Item item = ((InventoryUpdate)diff).Contents;
					Item oldItem = MyHero.GetSlot(((InventoryUpdate)diff).Slot);	// get the current contents of the slot
					int slot = ((InventoryUpdate)diff).Slot;

					// for now, we only care about items that can be purchased and are not consumable
					// we also need to track the case where and inventory slot's contents are set to empty

					// check for the case where the slot is emptied
					if (item.IsEmpty()) {
						// check whether we care about the item being removed from this slot
						if (oldItem != null && oldItem.IsEmpty() == false && 
							oldItem.IsConsumable() == false && oldItem.IsPurchasable() == true) {
							// check whether we are already tracking oldItem's id for this time step
							index = diffIds.IndexOf((int)(oldItem.Id));
							if (index != -1) {	// we are already tracking this item
								diffCounts[index]--;	// remove one instance of the item from the count
							}
							else {	// we need to add this item to the list of tracked items
								diffIds.Add((int)(oldItem.Id));	// add the id to the list of tracked ids
								diffCounts.Add(-1);	// init the count to record the removal of one instance
							}
						}
					}
					// check for the case where an item's charges have changed
					// but no items are added or removed
					// right now, we don't really do anything in this case
					else if (oldItem != null && oldItem.Id == item.Id) {
						// do nothing for now
					}
					// check for the case where a new item is being added
					// possibly replacing an old item (e.g., the old item is consumed in an updgrade)
					else {
						// if there was already an item in the slot, and we care about it, decrement its count
						if (oldItem != null && oldItem.IsEmpty() == false && oldItem.IsConsumable() == false
							&& oldItem.IsPurchasable() == true) {
							// check whether we are already tracking the old item's id for this time step
							index = diffIds.IndexOf((int)(oldItem.Id));
							if (index != -1) {	// we are already tracking this item
								diffCounts[index]--;	// remove one instance of the item from the count
							}
							else {	// we need to add this item to the list of tracked items
								diffIds.Add((int)(oldItem.Id));	// add the id to the list of tracked ids
								diffCounts.Add(-1);	// init the count to record the removal of one instance
							}
						}

						// check whether we care about the new item, and if so, increment its count
						if (item != null && item.IsEmpty() == false && item.IsConsumable() == false
							&& item.IsPurchasable() == true) {
							// check whether we are already tracking the item's id for this tiem step
							index = diffIds.IndexOf((int)(item.Id));
							if (index != -1) {	// we are already tracking this item
								diffCounts[index]++;	// add one instance of the item to the count
							}
							else {	// we need to add this item to the list of tracked items
								diffIds.Add((int)(item.Id));	// add the id to the list of tracked ids
								diffCounts.Add(1);	// init the count to record the addition of one instance
							}
						}
					}
				}
			}
			// update the agent's inventory totals based on the lists of items that have changed counts this time step
			bool itemsChanged = false;
			for (int i = 0; i < diffIds.Count; i++) {
				// if the count is 0, then there was no net change
				// 	- just moved from one slot to another, or the item's charges changed
				if (diffCounts[i] != 0)	{	// there was a net change + or -
					Items[diffIds[i]] += diffCounts[i];

					itemsChanged = true;
				}
			}
			if (verbose && itemsChanged == true) {
				int id = 0;
				Console.Write(MyHero.GetCurrentStep().Tick + ": ");
				if (purchasedItems.Count > 0) {
					Console.Write("(");
					foreach (Item item in purchasedItems) {
						if (item.IsUpgrage()) {
							Console.Write("+" + item.Id.ToString().ToUpper());
						}
						else {
							Console.Write("+" + item.Id.ToString());
						}
					}
					Console.Write(") : ");
				}
				foreach (int count in Items) {
					if (count != 0) {
						if (Item.IsUpgrade((ItemId)(id)) == true) {
							Console.Write(((ItemId)id).ToString().ToUpper() + "=" + count + " ");
						}
						else {
							Console.Write(((ItemId)id).ToString() + "=" + count + " ");
						}
					}
					id++;
				}

				Console.WriteLine("");
			}

			// update the hero to the current time step
			// Note: this will also increment the Hero's enumerator if need be
			MyHero.DoTimeStep(MyHero.GetCurrentStep().Tick);
		}

		public void Run() {
			if (MyHero == null) return;

			MyHero.InitReplay();	// set the associated hero's time step enumerator to the first time step

			if (MyHero.IsEnumeratorValid() == false) {
				return;
			}

			int tick;

			// iterate through each time step in which the hero has a state change
			while (MyHero.IsEnumeratorValid() == true) {
				tick = MyHero.GetCurrentStep().Tick;

				bool verbose = true;
				UpdateState(tick, verbose);
			}
		}

		public void RunTaskStep(int tick, ref int choicesMade, ref int correctChoicesMade) {
			// check that we have a valid enumerator
			// also check that the time of the hero's next state update matches the current tick
			//	otherwise, there's nothing for us to do here until the simulation advances further
			if (MyHero.IsEnumeratorValid() == false || MyHero.GetCurrentStep().Tick > tick) {
				return;
			}


				
			List<Item> purchasedItems = new List<Item>();

			// pull out any purchases made by the player at this time step
			foreach (StateChange diff in MyHero.GetCurrentStep().Diffs) {
				if (diff.Type == UpdateType.ItemPurchase) {
					Item newItem = ((ItemPurchase)diff).NewItem;
					if (newItem.IsConsumable() == false && newItem.IsPurchasable() == true) {
						purchasedItems.Add(((ItemPurchase)diff).NewItem);
					}
				}
			}

			/*
			MyAgent.ACS.Parameters.FIXED_BL_LEVEL_SELECTION_MEASURE = 0.0;
			MyAgent.ACS.Parameters.FIXED_RER_LEVEL_SELECTION_MEASURE = 0.0;
			MyAgent.ACS.Parameters.FIXED_IRL_LEVEL_SELECTION_MEASURE = 0;
			MyAgent.ACS.Parameters.FIXED_FR_LEVEL_SELECTION_MEASURE = 1.0;
			*/
				

			// for each purchase made by the player at this step, give the agent a chance to try to make the
			//	same purchase
			// Note: the state may change without a purchase being made, in which case there is nothing for the agent to do,
			//	we should just update the state and move on (in the calling function, for now)
			// Note: it is possible for the log file to record more than one purchase for a single time step,
			//	this seems to be an artifact of the way missing components can be automatically purchased to upgrade an item.
			//	It doesn't seem to happen often, and I'm not sure why it happens some times and not others.
			//	In such a case, we will "peek ahead" and add the purchased item into the inventory before asking the agent
			//	to make decisions corresponding to each subsequent purchase.
			//	After we have run through all the purchases, we will remove these purchases from the inventory counts and
			//	update normally (which will add them back in).
			foreach (Item item in purchasedItems) {

				// if we are using imitative learning, learning by example, supervised learning...
				// we are going to temporarily create a fixed rule in clarion, that suggests to the agent
				//	that it perform the same action that the human took
				// we will then retract the rule after the agent makes its choice
				// Note: another (possibly better) approach would be to set up fixed rules for each possible
				//	action the human might take and leave them all active
				// As far as I currently know, there is no way to specify a single rule that simple says
				// "if human did x, do x", we have to explicitly specify a new rule for each possible action

				// get the action chunk corresponding to the action the human player took
				PlayerChoice = PurchaseActions[(int)(item.Id)];

				// set up a fixed rule that specifies the same action the human player took
				// since the rule should only be active when it is relevant, we shouldn't really need to
				//	bother with calculating a support value. If it is active, it should have a fixed support of 1.
//				FixedRule imitateRule = AgentInitializer.InitializeActionRule(MyAgent, FixedRule.Factory, playerChoice, ImitativeSupportDelegate);
//				IRLRule imitateRule = AgentInitializer.InitializeActionRule(MyAgent, IRLRule.Factory, playerChoice, ImitativeSupportDelegate);
//				MyAgent.Commit(imitateRule);


				// sensory input to the Calrion agent
				//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
				//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
				// do we need to be creating a new object every time?
				// can we reuse the old and only change updated percepts?
				// is this currently a big performance hit?
				//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
				//!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!
				SensoryInformation sensoryInput = World.NewSensoryInformation(MyAgent);

				// output from the Clarion agent
				ExternalActionChunk myChoice = null;


				// do perception

				// perceive the hero's current level
				for (int i = 0; i < 25; i++) {
					if (i == MyHero.Level - 1) {
						sensoryInput.Add(LevelInputs[MyHero.Level - 1], MyAgent.Parameters.MAX_ACTIVATION);
					}
					else {
						sensoryInput.Add(LevelInputs[i], MyAgent.Parameters.MIN_ACTIVATION);
					}
				}

				// set the input that corresponds to the count of each item to maximum
				// I **think** it is not neccessary to set the other possible inputs (i.e., for different count numbers) to minimum
				//	but I could be wrong about that
				// Note: we are assuming count will never be > 3 - it could be (buy more than 3 copies of an item), but there is never
				//	likely to be a reason to do so
				for (int i = 0; i < Items.Length; i++) {
//					Console.Write(((ItemId)i).ToString() + "=" + Items[i] + "; ");
					for (int j = 0; j < 4; j++) {
						if (j == Items[i]) {
							sensoryInput.Add(InventoryInputs[i, Items[i]], MyAgent.Parameters.MAX_ACTIVATION);
						}
						else {
							sensoryInput.Add(InventoryInputs[i, j], MyAgent.Parameters.MIN_ACTIVATION);
						}
					}

/*					if (Items[i] > 0) {
						sensoryInput.Add(InventoryInputs[i, Items[i]], MyAgent.Parameters.MAX_ACTIVATION);
					}
*/
				}
//				Console.WriteLine("");

				// perceive
				MyAgent.Perceive(sensoryInput);
				// and choose an action
				myChoice = MyAgent.GetChosenExternalAction(sensoryInput);

				choicesMade++;

				// deliver appropriate feedback
				if (myChoice == PlayerChoice) {	// agent was right
					MyAgent.ReceiveFeedback(sensoryInput, 1.0);
//					Console.WriteLine(tick + ": " + MyHero.Name + ": player and agent bought: " + item.Id.ToString());
					correctChoicesMade++;
				}
				else {
					MyAgent.ReceiveFeedback(sensoryInput, 0.0);	// agent was wrong
//					String choiceString = myChoice.ToString().Split(':')[0].Split(' ')[1];
//					Console.WriteLine(tick + ": " + MyHero.Name + ": player bought: " + item.Id.ToString() + "; agent bought: " + choiceString);
				}

				// if we have created a fixed rule to support imitation of the human player, then
				//	retract (remove) it here
//				MyAgent.Retract(imitateRule, Agent.InternalContainers.ACTION_RULES);
//				MyAgent.Remove(imitateRule);

				// increment the count for the item purchased by the player (this will get decremented later, then updated based on the inventory state)
				// this is to handle the case where more than one purchase is recorded for a single time step (something a human can't actually do,
				//	but that the software may record as a result of automated purchases during an upgrade)
				Items[(int)(item.Id)]++;
			}

			// decrement the count of each purchased item
			// this is to handle the case where more than one purchase is recorded for a single time step (something a human can't actually do,
			//	but that the software may record as a result of automated purchases during an upgrade)
			foreach(Item item in purchasedItems) {
				Items[(int)(item.Id)]--;
			}
		}


		// ok, if we are using a fixed rule for imitative learning that should only be valid when it is needed,
		//	the we don't really need to calculate a value for its support, we can just return 1
		// ... maybe?
		public double ReturnFixedSupport(ActivationCollection currentInput, Clarion.Framework.Core.Rule r) {
			if (PlayerChoice != null && r.OutputChunk == PlayerChoice) {
				return 1;
			}

			return 0;
		}

		// C# delegate that just passes the ReturnFixedSupport method above
		public SupportCalculator ImitativeSupportDelegate {
			get {return ReturnFixedSupport;}
		}

		// attempt to output the Clarion agent's current state to file
		// directory specifies the path to the directory to write to
		// the file name will be <hero's name>.bin
/*		public void WriteAgentToFile(string directoryPath) {

			if (MyAgent != null && MyHero != null) {
				Directory.CreateDirectory(directoryPath);
				Stream outStream = File.Open(directoryPath + MyHero.Name + ".bin", FileMode.Create);
				BinaryFormatter bFormatter = new BinaryFormatter();

				SerializableAgent serializableAgent = new SerializableAgent(MyAgent);
				bFormatter.Serialize(outStream, serializableAgent);
				outStream.Close();
			}

		}
*/
		// attempt to output the Clarion agent's current state to an XML file
		// directory specifies the path to the directory to write to
		// the file name will be <hero's name>.xml
/*		public void WriteAgentToXML(string directoryPath) {
			if (MyAgent == null || MyHero == null) return;

			Clarion.Plugins.SerializationPlugin.SerializeWorld("test.txt");

			XmlSerializer serializer = new XmlSerializer(typeof(Agent));
			TextWriter textWriter = new StreamWriter(directoryPath + MyHero.Name + ".xml");
			serializer.Serialize(textWriter, MyAgent);
			textWriter.Close();
		}
*/
		public void KillAgent() {
			MyAgent.Die();
		}
	}
}

