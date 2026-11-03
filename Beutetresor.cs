using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Beutetresor
{
	// Schiebt waehrend des Einsatzes Erkennungsmarken und Schluessel aus
	// Rucksack, Weste und Taschen selbsttaetig in den Sicherheitsbehaelter.
	[BepInPlugin("com.kaan.beutetresor", "Beutetresor", "1.3.0")]
	[BepInDependency("com.SPT.core")]
	public class MainPlugin : BaseUnityPlugin
	{
		private const EquipmentSlot SlotRucksack = (EquipmentSlot)4;
		private const EquipmentSlot SlotSicherheitsbehaelter = (EquipmentSlot)5;
		private const EquipmentSlot SlotWeste = (EquipmentSlot)6;
		private const EquipmentSlot SlotTaschen = (EquipmentSlot)8;

		internal static ConfigEntry<bool> Eingeschaltet;
		internal static ConfigEntry<bool> NimmErkennungsmarken;
		internal static ConfigEntry<bool> NimmSchluessel;
		internal static ConfigEntry<bool> NimmGeld;
		internal static ConfigEntry<bool> AusRucksack;
		internal static ConfigEntry<bool> AusWeste;
		internal static ConfigEntry<bool> AusTaschen;
		internal static ConfigEntry<float> Pruefabstand;
		internal static ConfigEntry<bool> MeldungAnzeigen;
		internal static ConfigEntry<bool> Protokoll;
		internal static ConfigEntry<bool> InFaecherEinsortieren;
		internal static ConfigEntry<bool> RucksackEtuisNutzen;
		internal static ConfigEntry<bool> TresorNachsortieren;

		private static FieldInfo _feldSteuerung;
		private static PropertyInfo _eigenschaftSteuerung;
		private static bool _steuerungGesucht;

		private float _naechstePruefung;

		// Solange ein Inventar- oder Beutefenster offen ist, wird nicht umgeraeumt.
		// Grund: Ein Zug waehrend geoeffnetem Fenster veraendert die Gitter unter der
		// laufenden Anzeige - die Oberflaeche zeigt dann veraltete oder doppelte Inhalte.
		//
		// Nicht allein auf Close() verlassen: Wird der Bildschirm auf anderem Weg
		// weggeschaltet (Leiche pluendern, Tod, Ausgang), kommt Close() nicht an
		// und die Sperre blieb in 1.2.0 fuer den Rest des Einsatzes haengen -
		// dann wurde gar nichts mehr einsortiert. Deshalb merken wir uns das
		// Fenster und sehen bei jedem Takt nach, ob es wirklich noch sichtbar ist.
		private static bool _fensterOffen;
		private static MonoBehaviour _offenesFenster;
		private static float _naechstePruefungStatisch;

		internal static void FensterGeoeffnet(object __instance)
		{
			_fensterOffen = true;
			_offenesFenster = __instance as MonoBehaviour;
		}

		internal static void FensterGeschlossen()
		{
			_fensterOffen = false;
			_offenesFenster = null;
			_naechstePruefungStatisch = 0f;   // gleich nach dem Schliessen einmal aufraeumen
		}

		private static bool FensterWirklichOffen()
		{
			if (!_fensterOffen)
			{
				return false;
			}
			if (_offenesFenster == null || !_offenesFenster.gameObject.activeInHierarchy)
			{
				FensterGeschlossen();
				return false;
			}
			return true;
		}

		private void Awake()
		{
			Eingeschaltet = Config.Bind("1. Allgemein", "Eingeschaltet", true,
				"Raeumt waehrend des Einsatzes selbsttaetig in den Sicherheitsbehaelter um.");
			InFaecherEinsortieren = Config.Bind("1. Allgemein", "In Faecher im Tresor einsortieren", true,
				"Legt jeden Fund in den passenden Behaelter IM Tresor - Schluessel ins\n"
				+ "Schluesseletui, Marken ins Marken-Etui - statt lose ins Raster.\n"
				+ "Welcher Behaelter passt, entscheiden dessen eigene Filter; Faecher\n"
				+ "ohne Filter kommen zuletzt dran, damit ein grosser Beutel nichts\n"
				+ "wegschnappt. Passt kein Fach, landet der Fund wie bisher im Tresor.\n"
				+ "Behaelter in Behaeltern werden mit durchsucht.");
			RucksackEtuisNutzen = Config.Bind("1. Allgemein", "Etuis im Rucksack nutzen", true,
				"Ist im Tresor kein passendes Etui (oder es ist voll), wandert der Fund\n"
				+ "in ein passendes Etui im Rucksack - etwa ein Marken-Etui. Erst wenn\n"
				+ "auch dort nichts passt, landet er lose im Tresor.");
			TresorNachsortieren = Config.Bind("1. Allgemein", "Im Tresor nachsortieren", true,
				"Marken, Schluessel und Geld, die lose im Tresor oder in einem Beutel\n"
				+ "darin liegen, in das passende Etui im Tresor umlegen.");
			NimmErkennungsmarken = Config.Bind("2. Was", "Erkennungsmarken", true,
				"Dogtags von erledigten Gegnern in den Sicherheitsbehaelter legen.");
			NimmSchluessel = Config.Bind("2. Was", "Schluessel", true,
				"Schluessel und Schluesselkarten in den Sicherheitsbehaelter legen.");
			NimmGeld = Config.Bind("2. Was", "Geld", true,
				"Rubel, Dollar, Euro und GP-Muenzen in den Sicherheitsbehaelter legen.\n"
				+ "Liegt eine Geldboerse darin, wandert es dank ihrer Filter von selbst\n"
				+ "dort hinein.");
			AusRucksack = Config.Bind("3. Woher", "Rucksack", true, "Auch aus dem Rucksack holen.");
			AusWeste = Config.Bind("3. Woher", "Weste", true, "Auch aus der Weste holen.");
			AusTaschen = Config.Bind("3. Woher", "Taschen", true, "Auch aus den Hosentaschen holen.");
			Pruefabstand = Config.Bind("4. Takt", "Pruefabstand (Sekunden)", 1f,
				new ConfigDescription("Wie oft nachgesehen wird. Kleiner heisst schneller, kostet aber minimal Leistung.",
					new AcceptableValueRange<float>(0.25f, 10f), Array.Empty<object>()));
			MeldungAnzeigen = Config.Bind("5. Anzeige", "Meldung im Spiel", true,
				"Blendet kurz ein, was in den Sicherheitsbehaelter gewandert ist.");
			Protokoll = Config.Bind("5. Anzeige", "Ins Protokoll schreiben", false,
				"Schreibt jede Umlagerung in die BepInEx-Logdatei.");
			FensterwaechterSetzen();
			Logger.LogInfo("[Beutetresor] Aktiv.");
		}

		// Haengt sich an Oeffnen und Schliessen des Inventarbildschirms.
		private void FensterwaechterSetzen()
		{
			try
			{
				MethodInfo zeigen = typeof(InventoryScreen)
					.GetMethods(BindingFlags.Instance | BindingFlags.Public)
					.FirstOrDefault(m => m.Name == "Show"
						&& m.GetParameters().Any(pp => pp.ParameterType == typeof(CompoundItem)));
				MethodInfo schliessen = AccessTools.Method(typeof(InventoryScreen), "Close", null, null);
				if (zeigen == null || schliessen == null)
				{
					Logger.LogWarning("[Beutetresor] Fensterwaechter nicht gesetzt: Methode nicht gefunden.");
					return;
				}
				Harmony harmony = new Harmony("com.kaan.beutetresor.fensterwaechter");
				harmony.Patch(zeigen, postfix: new HarmonyMethod(typeof(MainPlugin), nameof(FensterGeoeffnet)));
				harmony.Patch(schliessen, prefix: new HarmonyMethod(typeof(MainPlugin), nameof(FensterGeschlossen)));
				Logger.LogInfo("[Beutetresor] Fensterwaechter aktiv.");
			}
			catch (Exception ex)
			{
				Logger.LogError("[Beutetresor] Fensterwaechter fehlgeschlagen: " + ex);
			}
		}

		private void Update()
		{
			if (!Eingeschaltet.Value || FensterWirklichOffen())
			{
				return;
			}
			if (_naechstePruefungStatisch == 0f)
			{
				_naechstePruefung = 0f;          // Fenster wurde eben geschlossen
				_naechstePruefungStatisch = -1f;
			}
			if (Time.time < _naechstePruefung)
			{
				return;
			}
			_naechstePruefung = Time.time + Pruefabstand.Value;
			try
			{
				Aufraeumen();
			}
			catch (Exception ex)
			{
				Logger.LogError("[Beutetresor] Fehler beim Umraeumen: " + ex);
			}
		}

		// Holt den Inventar-Steuerer des Spielers. Bewusst ueber Reflection:
		// der Membername wandert zwischen EFT-Fassungen.
		private static InventoryController SteuererHolen(Player spieler)
		{
			if (spieler == null)
			{
				return null;
			}
			if (!_steuerungGesucht)
			{
				_steuerungGesucht = true;
				Type t = spieler.GetType();
				_eigenschaftSteuerung = t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
					.FirstOrDefault(p => typeof(InventoryController).IsAssignableFrom(p.PropertyType) && p.GetIndexParameters().Length == 0);
				if (_eigenschaftSteuerung == null)
				{
					_feldSteuerung = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
						.FirstOrDefault(f => typeof(InventoryController).IsAssignableFrom(f.FieldType));
				}
			}
			try
			{
				if (_eigenschaftSteuerung != null)
				{
					return _eigenschaftSteuerung.GetValue(spieler) as InventoryController;
				}
				if (_feldSteuerung != null)
				{
					return _feldSteuerung.GetValue(spieler) as InventoryController;
				}
			}
			catch
			{
			}
			return null;
		}

		private static bool Passt(Item gegenstand)
		{
			if (gegenstand == null)
			{
				return false;
			}
			if (NimmErkennungsmarken.Value && gegenstand.GetItemComponent<DogtagComponent>() != null)
			{
				return true;
			}
			if (NimmSchluessel.Value && gegenstand.GetItemComponent<KeyComponent>() != null)
			{
				return true;
			}
			// Geld hat keine eigene Komponente - das Spiel beantwortet die
			// Frage ueber die Vorlagenkennung. CurrencyUtil kennt dabei auch
			// die Ausweichkennungen (etwa den grossen Rubelstapel), deshalb
			// nicht selbst eine Liste von IDs pflegen.
			if (NimmGeld.Value && IstGeld(gegenstand))
			{
				return true;
			}
			return false;
		}

		private static bool IstGeld(Item gegenstand)
		{
			try
			{
				return CurrencyUtil.IsCurrencyId(gegenstand.TemplateId);
			}
			catch
			{
				return false;
			}
		}

		// Laeuft einen Gegenstand samt Inhalt ab. Ueber CompoundItem, nicht ueber
		// den obfuskierten Basisklassennamen.
		private static IEnumerable<Item> AlleGegenstaende(Item wurzel)
		{
			if (wurzel == null)
			{
				yield break;
			}
			yield return wurzel;
			CompoundItem halter = wurzel as CompoundItem;
			if (halter == null)
			{
				yield break;
			}
			foreach (EFT.InventoryLogic.IContainer behaelter in halter.Containers)
			{
				foreach (Item kind in behaelter.Items)
				{
					if (kind == null || kind == wurzel)
					{
						continue;
					}
					foreach (Item enkel in AlleGegenstaende(kind))
					{
						yield return enkel;
					}
				}
			}
		}

		/// <summary>Sucht einen Platz fuer den Gegenstand - erst in den
		/// Behaeltern IM Tresor (Schluesseletui, Marken-Etui, Mappe), dann im
		/// Tresor selbst.
		///
		/// WARUM DIE REIHENFOLGE: Der Tresor nimmt fast alles an. Wer zuerst
		/// ihn fragt, bekommt immer ein Ja - und der Schluessel landet lose
		/// im Raster statt im Etui, obwohl eines da ist. Also erst die
		/// Fachbehaelter fragen; sagt keiner ja, bleibt der Tresor.
		///
		/// Die Filter machen die Arbeit: Ein Schluesseletui nimmt nur
		/// Schluessel, TryFindLocationForItem weist alles andere von selbst
		/// ab. Wir muessen also nicht wissen, WAS ein Behaelter ist.</summary>
		///
		/// 1.3.0: Reihenfolge der Ziele
		///   1. Etuis im Tresor (mit Filtern, auch Etui im Beutel im Tresor)
		///   2. Etuis im Rucksack
		///   3. Beutel ohne Filter im Tresor
		///   4. der Tresor selbst
		/// Liegt der Fund schon im Tresor, kommen nur Etuis im Tresor in Frage -
		/// aus dem sicheren Tresor wandert nichts zurueck in den Rucksack.</summary>
		private static ItemAddress FreienPlatzSuchen(Ziele ziele, Item gegenstand, bool schonImTresor)
		{
			CompoundItem jetzigerHalter = Halter(gegenstand);

			if (InFaecherEinsortieren.Value)
			{
				ItemAddress a = InFaechernSuchen(ziele.EtuisTresor, gegenstand, jetzigerHalter);
				if (a != null || schonImTresor)
				{
					return a;
				}
				if (RucksackEtuisNutzen.Value)
				{
					a = InFaechernSuchen(ziele.EtuisRucksack, gegenstand, jetzigerHalter);
					if (a != null)
					{
						return a;
					}
				}
				a = InFaechernSuchen(ziele.BeutelTresor, gegenstand, jetzigerHalter);
				if (a != null)
				{
					return a;
				}
			}
			else if (schonImTresor)
			{
				return null;
			}

			return ImGitterSuchen(ziele.Tresor, gegenstand);
		}

		private static ItemAddress InFaechernSuchen(List<CompoundItem> faecher, Item gegenstand, CompoundItem jetzigerHalter)
		{
			for (int i = 0; i < faecher.Count; i++)
			{
				// Nicht in den Behaelter "umziehen", in dem er schon liegt,
				// und nicht in sich selbst.
				if ((object)faecher[i] == (object)jetzigerHalter || (object)faecher[i] == (object)gegenstand)
				{
					continue;
				}
				ItemAddress a = ImGitterSuchen(faecher[i], gegenstand);
				if (a != null)
				{
					return a;
				}
			}
			return null;
		}

		// Der Behaelter, in dem der Gegenstand gerade liegt (oder null).
		private static CompoundItem Halter(Item gegenstand)
		{
			try
			{
				ItemAddress adresse = gegenstand.CurrentAddress;
				if (adresse == null || adresse.Container == null)
				{
					return null;
				}
				return adresse.Container.ParentItem as CompoundItem;
			}
			catch
			{
				return null;
			}
		}

		private sealed class Ziele
		{
			public CompoundItem Tresor;
			public List<CompoundItem> EtuisTresor = new List<CompoundItem>();
			public List<CompoundItem> BeutelTresor = new List<CompoundItem>();
			public List<CompoundItem> EtuisRucksack = new List<CompoundItem>();
			public HashSet<CompoundItem> AlleEtuis = new HashSet<CompoundItem>();
		}

		private static ItemAddress ImGitterSuchen(CompoundItem ziel, Item gegenstand)
		{
			if (ziel == null || ziel.Grids == null)
			{
				return null;
			}
			ItemAddress adresse = default(ItemAddress);
			EFT.InventoryLogic.Grid[] gitter = ziel.Grids;
			for (int i = 0; i < gitter.Length; i++)
			{
				if (gitter[i].TryFindLocationForItem(gegenstand, out adresse) && adresse != null)
				{
					return adresse;
				}
			}
			return null;
		}

		/// <summary>Sammelt alle Behaelter unterhalb von <paramref name="wurzel"/>,
		/// auch verschachtelte (Etui im Beutel im Tresor), getrennt nach
		/// "mit Filtern" (Etuis) und "ohne Filter" (Beutel).
		///
		/// RANGFOLGE: Ein Fach mit Filtern ist auf etwas Bestimmtes gemuenzt
		/// und geht vor; bei gleichem Rang gewinnt das kleinere Raster. So
		/// landet ein Schluessel im Schluesseletui und nicht in einem grossen
		/// Beutel, der zufaellig auch im Tresor liegt.</summary>
		private static void FaecherSammeln(CompoundItem wurzel, List<CompoundItem> etuis, List<CompoundItem> beutel)
		{
			if (wurzel == null || wurzel.Grids == null)
			{
				return;
			}
			EFT.InventoryLogic.Grid[] gitter = wurzel.Grids;
			for (int i = 0; i < gitter.Length; i++)
			{
				foreach (Item drin in gitter[i].Items)
				{
					CompoundItem fach = drin as CompoundItem;
					if (fach == null || fach.Grids == null || fach.Grids.Length == 0)
					{
						continue;
					}
					if (FachRang(fach) < 100000)
					{
						etuis.Add(fach);
					}
					else if (beutel != null)
					{
						beutel.Add(fach);
					}
					FaecherSammeln(fach, etuis, beutel);
				}
			}
		}

		private static void NachRangSortieren(List<CompoundItem> faecher)
		{
			faecher.Sort(delegate (CompoundItem a, CompoundItem b)
			{
				return FachRang(a).CompareTo(FachRang(b));
			});
		}

		private static Ziele ZieleSammeln(CompoundItem tresor, CompoundItem rucksack)
		{
			Ziele z = new Ziele();
			z.Tresor = tresor;
			FaecherSammeln(tresor, z.EtuisTresor, z.BeutelTresor);
			// Im Rucksack nur Etuis - Beutel im Rucksack sind keine Ablage,
			// sondern selbst Quelle.
			FaecherSammeln(rucksack, z.EtuisRucksack, null);
			NachRangSortieren(z.EtuisTresor);
			NachRangSortieren(z.BeutelTresor);
			NachRangSortieren(z.EtuisRucksack);
			foreach (CompoundItem e in z.EtuisTresor) z.AlleEtuis.Add(e);
			foreach (CompoundItem e in z.EtuisRucksack) z.AlleEtuis.Add(e);
			return z;
		}

		private static int FachRang(CompoundItem fach)
		{
			int flaeche = 0;
			bool mitFiltern = false;
			EFT.InventoryLogic.Grid[] gitter = fach.Grids;
			for (int i = 0; i < gitter.Length; i++)
			{
				flaeche += gitter[i].GridWidth * gitter[i].GridHeight;
				if (gitter[i].Filters != null && gitter[i].Filters.Length > 0)
				{
					mitFiltern = true;
				}
			}
			// Ohne Filter heisst "nimmt alles" - solche Behaelter ganz nach
			// hinten, damit sie nichts wegschnappen.
			return (mitFiltern ? 0 : 100000) + flaeche;
		}

		// Sagt bei eingeschaltetem Protokoll, WO das Umraeumen aussteigt.
		// Gedrosselt: jede Meldung hoechstens alle 5 Sekunden, sonst flutet
		// der 1-Sekunden-Takt das Log.
		private static readonly Dictionary<string, float> _letzteMeldung = new Dictionary<string, float>();

		private void Diagnose(string text)
		{
			if (!Protokoll.Value)
			{
				return;
			}
			float jetzt = Time.time;
			float vorher;
			if (_letzteMeldung.TryGetValue(text, out vorher) && jetzt - vorher < 5f)
			{
				return;
			}
			_letzteMeldung[text] = jetzt;
			Logger.LogInfo("[Beutetresor] " + text);
		}

		private void Aufraeumen()
		{
			GameWorld welt = Singleton<GameWorld>.Instance;
			// Eine GameWorld gibt es auch im Versteck (HideoutGameWorld) - und
			// sie bleibt nach dem Verlassen des Verstecks bis zum naechsten
			// Raidstart stehen (TarkovApplication.HideHideout gibt sie nicht
			// frei). Ihr MainPlayer ist die HideoutPlayer-Figur mit der echten
			// Profil-Inventarsteuerung. Ohne diese Pruefung raeumte der Mod
			// also auch im Versteck und im Hauptmenue um.
			if (welt == null || welt is HideoutGameWorld)
			{
				return;   // kein Einsatz - normal, nicht melden
			}
			Player spieler = welt.MainPlayer;
			if (spieler == null)
			{
				Diagnose("Abbruch: kein Spieler.");
				return;
			}
			InventoryController steuerung = SteuererHolen(spieler);
			if (steuerung == null || steuerung.Inventory == null)
			{
				Diagnose("Abbruch: Inventar-Steuerer nicht gefunden.");
				return;
			}
			InventoryEquipment ausruestung = steuerung.Inventory.Equipment;
			if (ausruestung == null)
			{
				Diagnose("Abbruch: keine Ausruestung.");
				return;
			}
			Slot tresorSlot = ausruestung.GetSlot(SlotSicherheitsbehaelter);
			CompoundItem tresor = (tresorSlot != null) ? (tresorSlot.ContainedItem as CompoundItem) : null;
			if (tresor == null)
			{
				Diagnose("Abbruch: kein Sicherheitsbehaelter im Slot.");
				return;
			}

			List<EquipmentSlot> quellen = new List<EquipmentSlot>();
			if (AusRucksack.Value) quellen.Add(SlotRucksack);
			if (AusWeste.Value) quellen.Add(SlotWeste);
			if (AusTaschen.Value) quellen.Add(SlotTaschen);

			Slot rucksackSlot = ausruestung.GetSlot(SlotRucksack);
			CompoundItem rucksack = (rucksackSlot != null) ? (rucksackSlot.ContainedItem as CompoundItem) : null;
			Ziele ziele = ZieleSammeln(tresor, RucksackEtuisNutzen.Value ? rucksack : null);

			// Fundstuecke sammeln. Was schon in einem Etui liegt (im Tresor oder
			// im Rucksack), ist einsortiert und bleibt liegen - sonst wuerde es
			// zwischen Rucksack-Etui und Tresor hin und her geschoben.
			List<Item> kandidaten = new List<Item>();
			HashSet<Item> imTresor = new HashSet<Item>();
			foreach (EquipmentSlot slot in quellen)
			{
				Slot s = ausruestung.GetSlot(slot);
				KandidatenSammeln((s != null) ? s.ContainedItem : null, ziele, kandidaten);
			}
			if (TresorNachsortieren.Value && InFaecherEinsortieren.Value && ziele.EtuisTresor.Count > 0)
			{
				int vorher = kandidaten.Count;
				KandidatenSammeln(tresor, ziele, kandidaten);
				for (int i = vorher; i < kandidaten.Count; i++)
				{
					imTresor.Add(kandidaten[i]);
				}
			}
			if (kandidaten.Count == 0)
			{
				Diagnose("Nichts zu tun: keine Marken/Schluessel/Geld ausserhalb der Etuis.");
				return;
			}
			Diagnose(kandidaten.Count + " Fundstueck(e); Etuis im Tresor: " + ziele.EtuisTresor.Count
				+ ", Beutel im Tresor: " + ziele.BeutelTresor.Count + ", Etuis im Rucksack: " + ziele.EtuisRucksack.Count + ".");

			List<string> verschoben = new List<string>();
			foreach (Item gegenstand in kandidaten)
			{
				bool schonImTresor = imTresor.Contains(gegenstand);
				ItemAddress adresse = FreienPlatzSuchen(ziele, gegenstand, schonImTresor);
				if (adresse == null)
				{
					if (!schonImTresor)
					{
						Diagnose("Kein Platz im Tresor fuer " + Anzeigename(gegenstand) + ".");
					}
					continue;
				}
				var zug = EFT.InventoryLogic.ItemManipulator.Move(gegenstand, adresse, (EFT.InventoryLogic.ItemController)(object)steuerung, true);
				if (!zug.Succeeded)
				{
					Diagnose("Zug abgelehnt fuer " + Anzeigename(gegenstand) + " (Move nicht erfolgreich).");
					continue;
				}
				EFT.InventoryLogic.IOperationResult vorgang = (EFT.InventoryLogic.IOperationResult)(object)zug.Value;
				if (vorgang == null || !((EFT.InventoryLogic.ItemController)steuerung).CanExecute(vorgang))
				{
					Diagnose("CanExecute verweigert fuer " + Anzeigename(gegenstand) + ".");
					continue;
				}
				((EFT.InventoryLogic.ItemController)steuerung).RunNetworkTransaction(vorgang, null);
				verschoben.Add(Anzeigename(gegenstand));
				if (Protokoll.Value)
				{
					CompoundItem neu = adresse.Container != null ? adresse.Container.ParentItem as CompoundItem : null;
					Logger.LogInfo("[Beutetresor] " + Anzeigename(gegenstand) + " -> " + (neu != null ? Anzeigename(neu) : "?"));
				}
			}

			if (verschoben.Count > 0 && MeldungAnzeigen.Value)
			{
				EFT.Communications.NotificationManager.DisplayMessageNotification(
					"Beutetresor: " + string.Join(", ", verschoben.ToArray()),
					(ENotificationDurationType)1, (ENotificationIconType)5, null);
			}
		}

		private static void KandidatenSammeln(Item wurzel, Ziele ziele, List<Item> kandidaten)
		{
			if (wurzel == null)
			{
				return;
			}
			foreach (Item g in AlleGegenstaende(wurzel))
			{
				if (g == wurzel || !Passt(g))
				{
					continue;
				}
				CompoundItem halter = Halter(g);
				if (halter != null && ziele.AlleEtuis.Contains(halter))
				{
					continue;
				}
				kandidaten.Add(g);
			}
		}

		private static MethodInfo _methodeName;
		private static bool _nameGesucht;

		private static string Anzeigename(Item gegenstand)
		{
			if (gegenstand == null)
			{
				return "?";
			}
			if (!_nameGesucht)
			{
				_nameGesucht = true;
				try
				{
					_methodeName = typeof(Item).Assembly.GetTypes()
						.SelectMany(t => t.GetMethods(BindingFlags.Static | BindingFlags.Public))
						.FirstOrDefault(m => m.Name == "LocalizedName" && m.ReturnType == typeof(string)
							&& m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Item));
				}
				catch
				{
					_methodeName = null;
				}
			}
			if (_methodeName != null)
			{
				try
				{
					string text = _methodeName.Invoke(null, new object[1] { gegenstand }) as string;
					if (!string.IsNullOrEmpty(text))
					{
						return text;
					}
				}
				catch
				{
				}
			}
			return gegenstand.StringTemplateId;
		}
	}
}
