using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Terraria.Audio;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader.Core;
using Terraria.ModLoader.IO;
using Terraria.UI;
using Terraria.Utilities;
using HookList = Terraria.ModLoader.Core.HookList<Terraria.ModLoader.GlobalItem>;

namespace Terraria.ModLoader
{
	/// <summary>
	/// This serves as the central class from which item-related functions are carried out. It also stores a list of mod items by ID.
	/// </summary>
	public static class ItemLoader
	{
		internal static readonly IList<ModItem> items = new List<ModItem>();
		internal static readonly IList<GlobalItem> globalItems = new List<GlobalItem>();
		internal static GlobalItem[] NetGlobals;
		internal static readonly ISet<int> animations = new HashSet<int>();
		internal static readonly int vanillaQuestFishCount = 41;
		internal static readonly int[] vanillaWings = new int[Main.maxWings];

		private static int nextItem = ItemID.Count;
		private static Instanced<GlobalItem>[] globalItemsArray = new Instanced<GlobalItem>[0];

		private static readonly List<HookList> hooks = new List<HookList>();
		private static readonly List<HookList> modHooks = new List<HookList>();

		private static HookList AddHook<F>(Expression<Func<GlobalItem, F>> func) {
			return AddHook(new HookList(ModLoader.Method(func)));
		}

		private static T AddHook<T>(T hook) where T : HookList<GlobalItem> {
			hooks.Add(hook);

			return hook;
		}

		public static HookList AddModHook(HookList hook) {
			hook.Update(globalItems);

			modHooks.Add(hook);

			return hook;
		}

		private static void FindVanillaWings() {
			if (vanillaWings[1] != 0)
				return;

			Item item = new Item();
			for (int k = 0; k < ItemID.Count; k++) {
				item.SetDefaults(k);
				if (item.wingSlot > 0) {
					vanillaWings[item.wingSlot] = k;
				}
			}
		}

		internal static int ReserveItemID() {
			if (ModNet.AllowVanillaClients)
				throw new Exception("Adding items breaks vanilla client compatibility");

			int reserveID = nextItem;
			nextItem++;
			return reserveID;
		}

		/// <summary>
		/// Gets the ModItem instance corresponding to the specified type. Returns null if no modded item has the given type.
		/// </summary>
		public static ModItem GetItem(int type) {
			return type >= ItemID.Count && type < ItemCount ? items[type - ItemID.Count] : null;
		}

		public static int ItemCount => nextItem;

		internal static void ResizeArrays(bool unloading) {
			//Textures
			Array.Resize(ref TextureAssets.Item, nextItem);
			Array.Resize(ref TextureAssets.ItemFlame, nextItem);

			//Sets
			LoaderUtils.ResetStaticMembers(typeof(ItemID), true);

			//Etc
			Array.Resize(ref Item.cachedItemSpawnsByType, nextItem);
			Array.Resize(ref Item.staff, nextItem);
			Array.Resize(ref Item.claw, nextItem);
			Array.Resize(ref Lang._itemNameCache, nextItem);
			Array.Resize(ref Lang._itemTooltipCache, nextItem);

			for (int k = ItemID.Count; k < nextItem; k++) {
				Lang._itemNameCache[k] = LocalizedText.Empty;
				Lang._itemTooltipCache[k] = ItemTooltip.None;
				Item.cachedItemSpawnsByType[k] = -1;
			}

			//Animation collections can be accessed during an ongoing (un)loading process.
			//Which is why the following 2 lines have to run without any interruptions.
			lock (Main.itemAnimationsRegistered) {
				Array.Resize(ref Main.itemAnimations, nextItem);

				Main.InitializeItemAnimations();
			}

			if (unloading)
				Array.Resize(ref Main.anglerQuestItemNetIDs, vanillaQuestFishCount);
			else
				Main.anglerQuestItemNetIDs = Main.anglerQuestItemNetIDs
					.Concat(items.Where(modItem => modItem.IsQuestFish()).Select(modItem => modItem.Type))
					.ToArray();

			FindVanillaWings();

			globalItemsArray = globalItems
				.Select(g => new Instanced<GlobalItem>(g.index, g))
				.ToArray();

			NetGlobals = ModLoader.BuildGlobalHook<GlobalItem, Action<Item, BinaryWriter>>(globalItems, g => g.NetSend);

			foreach (var hook in hooks.Union(modHooks)) {
				hook.Update(globalItems);
			}
		}

		internal static void Unload() {
			items.Clear();
			nextItem = ItemID.Count;
			globalItems.Clear();
			animations.Clear();
			modHooks.Clear();
		}

		internal static bool IsModItem(int index) => index >= ItemID.Count;

		private static bool GeneralPrefix(Item item) => item.maxStack == 1 && item.damage > 0 && item.ammo == 0 && !item.accessory;

		//Add all these to Terraria.Item.Prefix
		internal static bool MeleePrefix(Item item) => item.ModItem != null && GeneralPrefix(item) && item.melee && !item.noUseGraphic;
		internal static bool WeaponPrefix(Item item) => item.ModItem != null && GeneralPrefix(item) && item.melee && item.noUseGraphic;
		internal static bool RangedPrefix(Item item) => item.ModItem != null && GeneralPrefix(item) && item.ranged; //(item.ranged || item.thrown);
		internal static bool MagicPrefix(Item item) => item.ModItem != null && GeneralPrefix(item) && (item.magic || item.summon);

		private static HookList HookSetDefaults = AddHook<Action<Item>>(g => g.SetDefaults);

		internal static void SetDefaults(Item item, bool createModItem = true) {
			if (IsModItem(item.type) && createModItem)
				item.ModItem = GetItem(item.type).Clone(item);

			GlobalItem Instantiate(GlobalItem g)
				=> g.InstancePerEntity ? g.Clone(item, item) : g;

			LoaderUtils.InstantiateGlobals(item, globalItems, ref item.globalItems, Instantiate, () => {
				item.ModItem?.AutoDefaults();
				item.ModItem?.SetDefaults();
			});

			foreach (var g in HookSetDefaults.Enumerate(item.globalItems)) {
				g.SetDefaults(item);
			}
		}

		public static readonly HookList<GlobalItem, Action<Item, ItemCreationContext>> OnCreate = AddHook(
			new HookList<GlobalItem, Action<Item, ItemCreationContext>>(
				//Method reference
				g => g.OnCreate,
				//Invocation
				e => (Item item, ItemCreationContext context) => {
					foreach (var g in e.Enumerate(item.globalItems)) {
						g.OnCreate(item, context);
					}

					item.ModItem?.OnCreate(context);
				}
			)
		);

		internal static void DrawAnimatedItem(Item item, int whoAmI, Color color, Color alpha, float rotation, float scale) {
			int frameCount = Main.itemAnimations[item.type].FrameCount;
			int frameDuration = Main.itemAnimations[item.type].TicksPerFrame;

			Main.itemFrameCounter[whoAmI]++;

			if (Main.itemFrameCounter[whoAmI] >= frameDuration) {
				Main.itemFrameCounter[whoAmI] = 0;
				Main.itemFrame[whoAmI]++;
			}

			if (Main.itemFrame[whoAmI] >= frameCount) {
				Main.itemFrame[whoAmI] = 0;
			}

			var texture = TextureAssets.Item[item.type].Value;

			Rectangle frame = texture.Frame(1, frameCount, 0, Main.itemFrame[whoAmI]);
			float offX = item.width * 0.5f - frame.Width * 0.5f;
			float offY = item.height - frame.Height;

			Main.spriteBatch.Draw(texture, new Vector2(item.position.X - Main.screenPosition.X + frame.Width / 2 + offX, item.position.Y - Main.screenPosition.Y + frame.Height / 2 + offY), new Rectangle?(frame), alpha, rotation, frame.Size() / 2f, scale, SpriteEffects.None, 0f);

			if (item.color != default) {
				Main.spriteBatch.Draw(texture, new Vector2(item.position.X - Main.screenPosition.X + frame.Width / 2 + offX, item.position.Y - Main.screenPosition.Y + frame.Height / 2 + offY), new Rectangle?(frame), item.GetColor(color), rotation, frame.Size() / 2f, scale, SpriteEffects.None, 0f);
			}
		}

		private static Rectangle AnimatedItemFrame(Item item) {
			int frameCount = Main.itemAnimations[item.type].FrameCount;
			int frameDuration = Main.itemAnimations[item.type].TicksPerFrame;

			return Main.itemAnimations[item.type].GetFrame(TextureAssets.Item[item.type].Value);
		}

		public static readonly HookList<GlobalItem, Func<Item, UnifiedRandom, int>> ChoosePrefix = AddHook(
			new HookList<GlobalItem, Func<Item, UnifiedRandom, int>>(
				//Method reference
				g => g.ChoosePrefix,
				//Invocation
				e => (Item item, UnifiedRandom rand) => {
					foreach (var g in e.Enumerate(item.globalItems)) {
						int pre = g.ChoosePrefix(item, rand);

						if (pre > 0)
							return pre;
					}

					if (item.ModItem != null) {
						int pre = item.ModItem.ChoosePrefix(rand);

						if (pre > 0)
							return pre;
					}

					return -1;
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, int, UnifiedRandom, bool?>> PrefixChance = AddHook(
			new HookList<GlobalItem, Func<Item, int, UnifiedRandom, bool?>>(
				//Method reference
				g => g.PrefixChance,
				//Invocation
				e => (Item item, int pre, UnifiedRandom rand) => {
					bool? result = null;

					foreach (var g in e.Enumerate(item.globalItems)) {
						bool? r = g.PrefixChance(item, pre, rand);

						if (r.HasValue)
							result = r.Value && (result ?? true);
					}

					if (item.ModItem != null) {
						bool? r = item.ModItem.PrefixChance(pre, rand);

						if (r.HasValue)
							result = r.Value && (result ?? true);
					}

					return result;
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, int, bool>> AllowPrefix = AddHook(
			new HookList<GlobalItem, Func<Item, int, bool>>(
				//Method reference
				g => g.AllowPrefix,
				//Invocation
				e => (Item item, int pre) => {
					bool result = true;

					foreach (var g in e.Enumerate(item.globalItems)) {
						result &= g.AllowPrefix(item, pre);
					}

					if (item.ModItem != null)
						result &= item.ModItem.AllowPrefix(pre);

					return result;
				}
			)
		);

		/// <summary>
		/// Returns the "and" operation on the results of ModItem.CanUseItem and all GlobalItem.CanUseItem hooks.
		/// Does not fail fast (every hook is called).
		/// </summary>
		/// <param name="item">The item.</param>
		/// <param name="player">The player holding the item.</param>
		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> CanUseItem = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.CanUseItem,
				//Invocation
				e => (Item item, Player player) => {
					bool flag = true;

					if (item.ModItem != null)
						flag &= item.ModItem.CanUseItem(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						flag &= g.CanUseItem(item, player);
					}

					return flag;
				}
			)
		);

		/// <summary>
		/// Calls ModItem.UseStyle and all GlobalItem.UseStyle hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, Rectangle>> UseStyle = AddHook(
			new HookList<GlobalItem, Action<Item, Player, Rectangle>>(
				//Method reference
				g => g.UseStyle,
				//Invocation
				e => (Item item, Player player, Rectangle heldItemFrame) => {
					if (item.IsAir)
						return;

					item.ModItem?.UseStyle(player, heldItemFrame);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.UseStyle(item, player, heldItemFrame);
					}
				}
			)
		);

		/// <summary>
		/// If the player is not holding onto a rope and is not in the middle of using an item, calls ModItem.HoldStyle and all GlobalItem.HoldStyle hooks.
		/// <br/> Returns whether or not the vanilla logic should be skipped.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, Rectangle>> HoldStyle = AddHook(
			new HookList<GlobalItem, Action<Item, Player, Rectangle>>(
				//Method reference
				g => g.HoldStyle,
				//Invocation
				e => (Item item, Player player, Rectangle heldItemFrame) => {
					if (item.IsAir || player.pulley || player.itemAnimation > 0)
						return;

					item.ModItem?.HoldStyle(player, heldItemFrame);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.HoldStyle(item, player, heldItemFrame);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.HoldItem and all GlobalItem.HoldItem hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> HoldItem = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				g => g.HoldItem,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.HoldItem(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.HoldItem(item, player);
					}
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, Player, float>> UseTimeMultiplier = AddHook(
			new HookList<GlobalItem, Func<Item, Player, float>>(
				//Method reference
				g => g.UseTimeMultiplier,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return 1f;

					float multiplier = item.ModItem?.UseTimeMultiplier(player) ?? 1f;

					foreach (var g in e.Enumerate(item.globalItems)) {
						multiplier *= g.UseTimeMultiplier(item, player);
					}

					return multiplier;
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, Player, float>> MeleeSpeedMultiplier = AddHook(
			new HookList<GlobalItem, Func<Item, Player, float>>(
				//Method reference
				g => g.MeleeSpeedMultiplier,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return 1f;

					float multiplier = item.ModItem?.MeleeSpeedMultiplier(player) ?? 1f;

					foreach (var g in e.Enumerate(item.globalItems)) {
						multiplier *= g.MeleeSpeedMultiplier(item, player);
					}

					return multiplier;
				}
			)
		);

		public delegate void DelegateGetHealLife(Item item, Player player, bool quickHeal, ref int healValue);

		/// <summary>
		/// Calls ModItem.GetHealLife, then all GlobalItem.GetHealLife hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateGetHealLife> GetHealLife = AddHook(
			new HookList<GlobalItem, DelegateGetHealLife>(
				//Method reference
				g => g.GetHealLife,
				//Invocation
				e => (Item item, Player player, bool quickHeal, ref int healValue) => {
					if (item.IsAir)
						return;

					item.ModItem?.GetHealLife(player, quickHeal, ref healValue);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.GetHealLife(item, player, quickHeal, ref healValue);
					}
				}
			)
		);

		public delegate void DelegateGetHealMana(Item item, Player player, bool quickHeal, ref int healValue);

		/// <summary>
		/// Calls ModItem.GetHealMana, then all GlobalItem.GetHealMana hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateGetHealMana> GetHealMana = AddHook(
			new HookList<GlobalItem, DelegateGetHealMana>(
				//Method reference
				g => g.GetHealMana,
				//Invocation
				e => (Item item, Player player, bool quickHeal, ref int healValue) => {
					if (item.IsAir)
						return;

					item.ModItem?.GetHealMana(player, quickHeal, ref healValue);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.GetHealMana(item, player, quickHeal, ref healValue);
					}
				}
			)
		);

		public delegate void DelegateModifyManaCost(Item item, Player player, ref float reduce, ref float mult);

		/// <summary>
		/// Calls ModItem.ModifyManaCost, then all GlobalItem.ModifyManaCost hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateModifyManaCost> ModifyManaCost = AddHook(
			new HookList<GlobalItem, DelegateModifyManaCost>(
				//Method reference
				g => g.ModifyManaCost,
				//Invocation
				e => (Item item, Player player, ref float reduce, ref float mult) => {
					if (item.IsAir)
						return;

					item.ModItem?.ModifyManaCost(player, ref reduce, ref mult);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyManaCost(item, player, ref reduce, ref mult);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.OnMissingMana, then all GlobalItem.OnMissingMana hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, int>> OnMissingMana = AddHook(
			new HookList<GlobalItem, Action<Item, Player, int>>(
				//Method reference
				g => g.OnMissingMana,
				//Invocation
				e => (Item item, Player player, int neededMana) => {
					if (item.IsAir)
						return;

					item.ModItem?.OnMissingMana(player, neededMana);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.OnMissingMana(item, player, neededMana);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.OnConsumeMana, then all GlobalItem.OnConsumeMana hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, int>> OnConsumeMana = AddHook(
			new HookList<GlobalItem, Action<Item, Player, int>>(
				//Method reference
				g => g.OnConsumeMana,
				//Invocation
				e => (Item item, Player player, int manaConsumed) => {
					if (item.IsAir)
						return;

					item.ModItem?.OnConsumeMana(player, manaConsumed);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.OnConsumeMana(item, player, manaConsumed);
					}
				}
			)
		);

		public delegate void DelegateModifyResearchSorting(Item item, ref ContentSamples.CreativeHelper.ItemGroup itemGroup);

		public static readonly HookList<GlobalItem, DelegateModifyResearchSorting> ModifyResearchSorting = AddHook(
			new HookList<GlobalItem, DelegateModifyResearchSorting>(
				//Method reference
				g => g.ModifyResearchSorting,
				//Invocation
				e => (Item item, ref ContentSamples.CreativeHelper.ItemGroup itemGroup) => {
					if (item.IsAir)
						return;

					item.ModItem?.ModifyResearchSorting(ref itemGroup);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyResearchSorting(item, ref itemGroup);
					}
				}
			)
		);

		public delegate void DelegateModifyWeaponDamage(Item item, Player player, ref StatModifier damage, ref float flat);

		/// <summary>
		/// Calls ModItem.HookModifyWeaponDamage, then all GlobalItem.HookModifyWeaponDamage hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateModifyWeaponDamage> ModifyWeaponDamage = AddHook(
			new HookList<GlobalItem, DelegateModifyWeaponDamage>(
				//Method reference
				g => g.ModifyWeaponDamage,
				//Invocation
				e => (Item item, Player player, ref StatModifier damage, ref float flat) => {
					if (item.IsAir)
						return;

					item.ModItem?.ModifyWeaponDamage(player, ref damage, ref flat);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyWeaponDamage(item, player, ref damage, ref flat);
					}
				}
			)
		);

		public delegate void DelegateModifyWeaponKnockback(Item item, Player player, ref StatModifier knockback, ref float flat);

		/// <summary>
		/// Calls ModItem.ModifyWeaponKnockback, then all GlobalItem.ModifyWeaponKnockback hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateModifyWeaponKnockback> ModifyWeaponKnockback = AddHook(
			new HookList<GlobalItem, DelegateModifyWeaponKnockback>(
				//Method reference
				g => g.ModifyWeaponKnockback,
				//Invocation
				e => (Item item, Player player, ref StatModifier knockback, ref float flat) => {
					if (item.IsAir)
						return;

					item.ModItem?.ModifyWeaponKnockback(player, ref knockback, ref flat);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyWeaponKnockback(item, player, ref knockback, ref flat);
					}
				}
			)
		);


		public delegate void DelegateModifyWeaponCrit(Item item, Player player, ref int crit);

		/// <summary>
		/// Calls ModItem.ModifyWeaponCrit, then all GlobalItem.ModifyWeaponCrit hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateModifyWeaponCrit> ModifyWeaponCrit = AddHook(
			new HookList<GlobalItem, DelegateModifyWeaponCrit>(
				//Method reference
				g => g.ModifyWeaponCrit,
				//Invocation
				e => (Item item, Player player, ref int crit) => {
					if (item.IsAir)
						return;

					item.ModItem?.ModifyWeaponCrit(player, ref crit);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyWeaponCrit(item, player, ref crit);
					}
				}
			)
		);

		/// <summary>
		/// If the item is a modded item, ModItem.checkProjOnSwing is true, and the player is not at the beginning of the item's use animation, sets canShoot to false.
		/// </summary>
		public static bool CheckProjOnSwing(Player player, Item item) {
			return item.ModItem == null || !item.ModItem.OnlyShootOnSwing || player.itemAnimation == player.itemAnimationMax - 1;
		}

		public delegate void DelegatePickAmmo(Item weapon, Item ammo, Player player, ref int type, ref float speed, ref int damage, ref float knockback);
		/// <summary>
		/// Calls ModItem.PickAmmo, then all GlobalItem.PickAmmo hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegatePickAmmo> PickAmmo = AddHook(
			new HookList<GlobalItem, DelegatePickAmmo>(
				//Method reference
				g => g.PickAmmo,
				//Invocation
				e => (Item weapon, Item ammo, Player player, ref int type, ref float speed, ref int damage, ref float knockback) => {
					ammo.ModItem?.PickAmmo(weapon, player, ref type, ref speed, ref damage, ref knockback);

					foreach (var g in e.Enumerate(ammo.globalItems)) {
						g.PickAmmo(weapon, ammo, player, ref type, ref speed, ref damage, ref knockback);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.ConsumeAmmo for the weapon, ModItem.ConsumeAmmo for the ammo, then each GlobalItem.ConsumeAmmo hook for the weapon and ammo, until one of them returns false. If all of them return true, returns true.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Item, Player, bool>> ConsumeAmmo = AddHook(
			new HookList<GlobalItem, Func<Item, Item, Player, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.ConsumeAmmo)),
				//Invocation
				e => (Item item, Item ammo, Player player) => {
					if (item.ModItem != null && !item.ModItem.ConsumeAmmo(player)
					|| ammo.ModItem != null && !ammo.ModItem.ConsumeAmmo(player))
						return false;

					foreach (var g in e.Enumerate(ammo.globalItems)) {
						if (!g.ConsumeAmmo(item, player) || !g.ConsumeAmmo(ammo, player))
							return false;
					}

					return true;
				}
			)
		);

		/// <summary>
		/// Calls ModItem.OnConsumeAmmo for the weapon, ModItem.OnConsumeAmmo for the ammo, then each GlobalItem.OnConsumeAmmo hook for the weapon and ammo.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Item, Player>> OnConsumeAmmo = AddHook(
			new HookList<GlobalItem, Action<Item, Item, Player>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.OnConsumeAmmo)),
				//Invocation
				e => (Item item, Item ammo, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.OnConsumeAmmo(player);
					ammo.ModItem?.OnConsumeAmmo(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.OnConsumeAmmo(item, player);
					}

					foreach (var g in e.Enumerate(ammo.globalItems)) {
						g.OnConsumeAmmo(item, player);
					}
				}
			)
		);

		public delegate bool DelegateShoot(Item item, Player player, ref Vector2 position, ref float speedX, ref float speedY, ref int type, ref int damage, ref float knockBack);

		/// <summary>
		/// Calls each GlobalItem.Shoot hook, then ModItem.Shoot, until one of them returns false. If all of them return true, returns true.
		/// </summary>
		/// <param name="item">The weapon item.</param>
		/// <param name="player">The player.</param>
		/// <param name="position">The shoot spawn position.</param>
		/// <param name="speedX">The speed x calculated from shootSpeed and mouse position.</param>
		/// <param name="speedY">The speed y calculated from shootSpeed and mouse position.</param>
		/// <param name="type">The projectile type choosen by ammo and weapon.</param>
		/// <param name="damage">The projectile damage.</param>
		/// <param name="knockBack">The projectile knock back.</param>
		/// <returns></returns>
		public static readonly HookList<GlobalItem, DelegateShoot> Shoot = AddHook(
			new HookList<GlobalItem, DelegateShoot>(
				//Method reference
				g => g.Shoot,
				//Invocation
				e => (Item item, Player player, ref Vector2 position, ref float speedX, ref float speedY, ref int type, ref int damage, ref float knockBack) => {
					bool result = true;

					foreach (var g in e.Enumerate(item.globalItems)) {
						result &= g.Shoot(item, player, ref position, ref speedX, ref speedY, ref type, ref damage, ref knockBack);
					}

					if (result && item.ModItem != null)
						return item.ModItem.Shoot(player, ref position, ref speedX, ref speedY, ref type, ref damage, ref knockBack);

					return result;
				}
			)
		);

		public delegate void DelegateUseItemHitbox(Item item, Player player, ref Rectangle hitbox, ref bool noHitbox);

		/// <summary>
		/// Calls ModItem.UseItemHitbox, then all GlobalItem.UseItemHitbox hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateUseItemHitbox> UseItemHitbox = AddHook(
			new HookList<GlobalItem, DelegateUseItemHitbox>(
				//Method reference
				g => g.UseItemHitbox,
				//Invocation
				e => (Item item, Player player, ref Rectangle hitbox, ref bool noHitbox) => {
					item.ModItem?.UseItemHitbox(player, ref hitbox, ref noHitbox);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.UseItemHitbox(item, player, ref hitbox, ref noHitbox);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.MeleeEffects and all GlobalItem.MeleeEffects hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, Rectangle>> MeleeEffects = AddHook(
			new HookList<GlobalItem, Action<Item, Player, Rectangle>>(
				//Method reference
				g => g.MeleeEffects,
				//Invocation
				e => (Item item, Player player, Rectangle hitbox) => {
					item.ModItem?.MeleeEffects(player, hitbox);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.MeleeEffects(item, player, hitbox);
					}
				}
			)
		);

		/// <summary>
		/// Gathers the results of ModItem.CanHitNPC and all GlobalItem.CanHitNPC hooks. 
		/// If any of them returns false, this returns false. 
		/// Otherwise, if any of them returns true then this returns true. 
		/// If all of them return null, this returns null.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Player, NPC, bool?>> CanHitNPC = AddHook(
			new HookList<GlobalItem, Func<Item, Player, NPC, bool?>>(
				//Method reference
				g => g.CanHitNPC,
				//Invocation
				e => (Item item, Player player, NPC target) => {
					bool? flag = null;

					foreach (var g in e.Enumerate(item.globalItems)) {
						bool? canHit = g.CanHitNPC(item, player, target);

						if (canHit.HasValue) {
							if (!canHit.Value)
								return false;

							flag = true;
						}
					}

					if (item.ModItem != null) {
						bool? canHit = item.ModItem.CanHitNPC(player, target);

						if (canHit.HasValue) {
							if (!canHit.Value)
								return false;

							flag = true;
						}
					}

					return flag;
				}
			)
		);

		public delegate void DelegateModifyHitNPC(Item item, Player player, NPC target, ref int damage, ref float knockBack, ref bool crit);
		/// <summary>
		/// Calls ModItem.ModifyHitNPC, then all GlobalItem.ModifyHitNPC hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateModifyHitNPC> ModifyHitNPC = AddHook(
			new HookList<GlobalItem, DelegateModifyHitNPC>(
				//Method reference
				g => g.ModifyHitNPC,
				//Invocation
				e => (Item item, Player player, NPC target, ref int damage, ref float knockBack, ref bool crit) => {
					item.ModItem?.ModifyHitNPC(player, target, ref damage, ref knockBack, ref crit);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyHitNPC(item, player, target, ref damage, ref knockBack, ref crit);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.OnHitNPC and all GlobalItem.OnHitNPC hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, NPC, int, float, bool>> OnHitNPC = AddHook(
			new HookList<GlobalItem, Action<Item, Player, NPC, int, float, bool>>(
				//Method reference
				g => g.OnHitNPC,
				//Invocation
				e => (Item item, Player player, NPC target, int damage, float knockBack, bool crit) => {
					item.ModItem?.OnHitNPC(player, target, damage, knockBack, crit);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.OnHitNPC(item, player, target, damage, knockBack, crit);
					}
				}
			)
		);

		/// <summary>
		/// Calls all GlobalItem.CanHitPvp hooks, then ModItem.CanHitPvp, until one of them returns false. 
		/// If all of them return true, this returns true.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Player, Player, bool>> CanHitPvp = AddHook(
			new HookList<GlobalItem, Func<Item, Player, Player, bool>>(
				//Method reference
				g => g.CanHitPvp,
				//Invocation
				e => (Item item, Player player, Player target) => {
					foreach (var g in e.Enumerate(item.globalItems)) {
						if (!g.CanHitPvp(item, player, target))
							return false;
					}

					return item.ModItem == null || item.ModItem.CanHitPvp(player, target);
				}
			)
		);

		public delegate void DelegateModifyHitPvp(Item item, Player player, Player target, ref int damage, ref bool crit);
		/// <summary>
		/// Calls ModItem.ModifyHitPvp, then all GlobalItem.ModifyHitPvp hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateModifyHitPvp> ModifyHitPvp = AddHook(
			new HookList<GlobalItem, DelegateModifyHitPvp>(
				//Method reference
				g => g.ModifyHitPvp,
				//Invocation
				e => (Item item, Player player, Player target, ref int damage, ref bool crit) => {
					item.ModItem?.ModifyHitPvp(player, target, ref damage, ref crit);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyHitPvp(item, player, target, ref damage, ref crit);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.OnHitPvp and all GlobalItem.OnHitPvp hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, Player, int, bool>> OnHitPvp = AddHook(
			new HookList<GlobalItem, Action<Item, Player, Player, int, bool>>(
				//Method reference
				g => g.OnHitPvp,
				//Invocation
				e => (Item item, Player player, Player target, int damage, bool crit) => {
					item.ModItem?.OnHitPvp(player, target, damage, crit);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.OnHitPvp(item, player, target, damage, crit);
					}
				}
			)
		);

		/// <summary>
		/// Returns true if any of ModItem.UseItem or GlobalItem.UseItem return true
		/// Does not fail fast (calls every hook)
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> UseItem = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.UseItem,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return false;

					bool flag = false;

					if (item.ModItem != null)
						flag |= item.ModItem.UseItem(player);

					foreach (var g in e.Enumerate(item.globalItems))
						flag |= g.UseItem(item, player);

					return flag;
				}
			)
		);

		/// <summary>
		/// If ModItem.ConsumeItem or any of the GlobalItem.ConsumeItem hooks returns false, sets consume to false.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> ConsumeItem = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.ConsumeItem,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return true;

					if (item.ModItem != null && !item.ModItem.ConsumeItem(player))
						return false;

					foreach (var g in e.Enumerate(item.globalItems)) {
						if (!g.ConsumeItem(item, player))
							return false;
					}

					OnConsumeItem.Invoke(item, player);

					return true;
				}
			)
		);

		/// <summary>
		/// Calls ModItem.OnConsumeItem and all GlobalItem.OnConsumeItem hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> OnConsumeItem = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				g => g.OnConsumeItem,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.OnConsumeItem(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.OnConsumeItem(item, player);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.UseItemFrame, then all GlobalItem.UseItemFrame hooks, until one of them returns true. Returns whether any of the hooks returned true.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> UseItemFrame = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				g => g.UseItemFrame,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.UseItemFrame(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.UseItemFrame(item, player);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.HoldItemFrame, then all GlobalItem.HoldItemFrame hooks, until one of them returns true. Returns whether any of the hooks returned true.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> HoldItemFrame = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				g => g.HoldItemFrame,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.HoldItemFrame(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.HoldItemFrame(item, player);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.AltFunctionUse, then all GlobalItem.AltFunctionUse hooks, until one of them returns true. Returns whether any of the hooks returned true.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> AltFunctionUse = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.AltFunctionUse,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return false;

					if (item.ModItem != null && item.ModItem.AltFunctionUse(player))
						return true;

					foreach (var g in e.Enumerate(item.globalItems)) {
						if (g.AltFunctionUse(item, player))
							return true;
					}

					return false;
				}
			)
		);

		//place at end of first for loop in Terraria.Player.UpdateEquips
		//  call ItemLoader.UpdateInventory(this.inventory[j], this)
		/// <summary>
		/// Calls ModItem.UpdateInventory and all GlobalItem.UpdateInventory hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> UpdateInventory = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				g => g.UpdateInventory,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.UpdateInventory(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.UpdateInventory(item, player);
					}
				}
			)
		);

		/// <summary>
		/// Hook at the end of Player.VanillaUpdateEquip can be called from modded slots for modded equipments
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> UpdateEquip = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				g => g.UpdateEquip,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.UpdateEquip(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.UpdateEquip(item, player);
					}
				}
			)
		);

		/// <summary>
		/// Hook at the end of Player.ApplyEquipFunctional can be called from modded slots for modded equipments
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player, bool>> UpdateAccessory = AddHook(
			new HookList<GlobalItem, Action<Item, Player, bool>>(
				//Method reference
				g => g.UpdateAccessory,
				//Invocation
				e => (Item item, Player player, bool hideVisual) => {
					if (item.IsAir)
						return;

					item.ModItem?.UpdateAccessory(player, hideVisual);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.UpdateAccessory(item, player, hideVisual);
					}
				}
			)
		);

		/// <summary>
		/// Hook at the end of Player.ApplyEquipVanity can be called from modded slots for modded equipments
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> UpdateVanity = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				g => g.UpdateVanity,
				//Invocation
				e => (Item item, Player player) => {
					if (item.IsAir)
						return;

					item.ModItem?.UpdateVanity(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.UpdateVanity(item, player);
					}
				}
			)
		);

		//at end of Terraria.Player.UpdateArmorSets call ItemLoader.UpdateArmorSet(this, this.armor[0], this.armor[1], this.armor[2])
		/// <summary>
		/// If the head's ModItem.IsArmorSet returns true, calls the head's ModItem.UpdateArmorSet. This is then repeated for the body, then the legs. Then for each GlobalItem, if GlobalItem.IsArmorSet returns a non-empty string, calls GlobalItem.UpdateArmorSet with that string.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Player, Item, Item, Item>> UpdateArmorSet = AddHook(
			new HookList<GlobalItem, Action<Player, Item, Item, Item>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.UpdateArmorSet)),
				//Invocation
				e => (Player player, Item head, Item body, Item legs) => {
					if (head.ModItem != null && head.ModItem.IsArmorSet(head, body, legs))
						head.ModItem.UpdateArmorSet(player);

					if (body.ModItem != null && body.ModItem.IsArmorSet(head, body, legs))
						body.ModItem.UpdateArmorSet(player);

					if (legs.ModItem != null && legs.ModItem.IsArmorSet(head, body, legs))
						legs.ModItem.UpdateArmorSet(player);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						string set = g.IsArmorSet(head, body, legs);

						if (!string.IsNullOrEmpty(set))
							g.UpdateArmorSet(player, set);
					}
				}
			)
		);

		/// <summary>
		/// If the player's head texture's IsVanitySet returns true, calls the equipment texture's PreUpdateVanitySet. This is then repeated for the player's body, then the legs. Then for each GlobalItem, if GlobalItem.IsVanitySet returns a non-empty string, calls GlobalItem.PreUpdateVanitySet, using player.head, player.body, and player.legs.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Player>> PreUpdateVanitySet = AddHook(
			new HookList<GlobalItem, Action<Player>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.PreUpdateVanitySet)),
				//Invocation
				e => (Player player) => {
					var headTexture = EquipLoader.GetEquipTexture(EquipType.Head, player.head);
					var bodyTexture = EquipLoader.GetEquipTexture(EquipType.Body, player.body);
					var legTexture = EquipLoader.GetEquipTexture(EquipType.Legs, player.legs);

					if (headTexture != null && headTexture.IsVanitySet(player.head, player.body, player.legs))
						headTexture.PreUpdateVanitySet(player);

					if (bodyTexture != null && bodyTexture.IsVanitySet(player.head, player.body, player.legs))
						bodyTexture.PreUpdateVanitySet(player);

					if (legTexture != null && legTexture.IsVanitySet(player.head, player.body, player.legs))
						legTexture.PreUpdateVanitySet(player);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						string set = g.IsVanitySet(player.head, player.body, player.legs);

						if (!string.IsNullOrEmpty(set))
							g.PreUpdateVanitySet(player, set);
					}
				}
			)
		);

		/// <summary>
		/// If the player's head texture's IsVanitySet returns true, calls the equipment texture's UpdateVanitySet. This is then repeated for the player's body, then the legs. Then for each GlobalItem, if GlobalItem.IsVanitySet returns a non-empty string, calls GlobalItem.UpdateVanitySet, using player.head, player.body, and player.legs.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Player>> UpdateVanitySet = AddHook(
			new HookList<GlobalItem, Action<Player>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.UpdateVanitySet)),
				//Invocation
				e => (Player player) => {
					var headTexture = EquipLoader.GetEquipTexture(EquipType.Head, player.head);
					var bodyTexture = EquipLoader.GetEquipTexture(EquipType.Body, player.body);
					var legTexture = EquipLoader.GetEquipTexture(EquipType.Legs, player.legs);

					if (headTexture != null && headTexture.IsVanitySet(player.head, player.body, player.legs))
						headTexture.UpdateVanitySet(player);

					if (bodyTexture != null && bodyTexture.IsVanitySet(player.head, player.body, player.legs))
						bodyTexture.UpdateVanitySet(player);

					if (legTexture != null && legTexture.IsVanitySet(player.head, player.body, player.legs))
						legTexture.UpdateVanitySet(player);

					foreach (var globalItem in e.Enumerate(globalItemsArray)) {
						string set = globalItem.IsVanitySet(player.head, player.body, player.legs);

						if (!string.IsNullOrEmpty(set))
							globalItem.UpdateVanitySet(player, set);
					}
				}
			)
		);

		/// <summary>
		/// If the player's head texture's IsVanitySet returns true, calls the equipment texture's ArmorSetShadows. This is then repeated for the player's body, then the legs. Then for each GlobalItem, if GlobalItem.IsVanitySet returns a non-empty string, calls GlobalItem.ArmorSetShadows, using player.head, player.body, and player.legs.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Player>> ArmorSetShadows = AddHook(
			new HookList<GlobalItem, Action<Player>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.ArmorSetShadows)),
				//Invocation
				e => (Player player) => {
					var headTexture = EquipLoader.GetEquipTexture(EquipType.Head, player.head);
					var bodyTexture = EquipLoader.GetEquipTexture(EquipType.Body, player.body);
					var legTexture = EquipLoader.GetEquipTexture(EquipType.Legs, player.legs);

					if (headTexture != null && headTexture.IsVanitySet(player.head, player.body, player.legs))
						headTexture.ArmorSetShadows(player);

					if (bodyTexture != null && bodyTexture.IsVanitySet(player.head, player.body, player.legs))
						bodyTexture.ArmorSetShadows(player);

					if (legTexture != null && legTexture.IsVanitySet(player.head, player.body, player.legs))
						legTexture.ArmorSetShadows(player);

					foreach (var globalItem in e.Enumerate(globalItemsArray)) {
						string set = globalItem.IsVanitySet(player.head, player.body, player.legs);

						if (!string.IsNullOrEmpty(set))
							globalItem.ArmorSetShadows(player, set);
					}
				}
			)
		);

		public delegate void DelegateSetMatch(int armorSlot, int type, bool male, ref int equipSlot, ref bool robes);
		/// <summary>
		/// Calls EquipTexture.SetMatch, then all GlobalItem.SetMatch hooks.
		/// </summary>   
		public static readonly HookList<GlobalItem, DelegateSetMatch> SetMatch = AddHook(
			new HookList<GlobalItem, DelegateSetMatch>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.SetMatch)),
				//Invocation
				e => (int armorSlot, int type, bool male, ref int equipSlot, ref bool robes) => {
					var texture = EquipLoader.GetEquipTexture((EquipType)armorSlot, type);

					texture?.SetMatch(male, ref equipSlot, ref robes);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.SetMatch(armorSlot, type, male, ref equipSlot, ref robes);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.CanRightClick, then all GlobalItem.CanRightClick hooks, until one of the returns true. If one of the returns true, returns Main.mouseRight. Otherwise, returns false.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, bool>> CanRightClick = AddHook(
			new HookList<GlobalItem, Func<Item, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.CanRightClick)),
				//Invocation
				e => (Item item) => {
					if (item.IsAir || !Main.mouseRight)
						return false;

					if (item.ModItem != null && item.ModItem.CanRightClick())
						return true;

					foreach (var g in e.Enumerate(item.globalItems)) {
						if (g.CanRightClick(item))
							return true;
					}

					return false;
				}
			)
		);

		/// <summary>
		/// If Main.mouseRightRelease is true, the following steps are taken:
		/// 1. Call ModItem.RightClick
		/// 2. Calls all GlobalItem.RightClick hooks
		/// 3. Call ItemLoader.ConsumeItem, and if it returns true, decrements the item's stack
		/// 4. Sets the item's type to 0 if the item's stack is 0
		/// 5. Plays the item-grabbing sound
		/// 6. Sets Main.stackSplit to 30
		/// 7. Sets Main.mouseRightRelease to false
		/// 8. Calls Recipe.FindRecipes.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, Player>> RightClick = AddHook(
			new HookList<GlobalItem, Action<Item, Player>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.RightClick)),
				//Invocation
				e => (Item item, Player player) => {
					if (!Main.mouseRightRelease)
						return;

					item.ModItem?.RightClick(player);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.RightClick(item, player);
					}

					if (ConsumeItem.Invoke(item, player) && --item.stack == 0)
						item.SetDefaults();

					SoundEngine.PlaySound(7);
					Main.stackSplit = 30;
					Main.mouseRightRelease = false;
					Recipe.FindRecipes();
				}
			)
		);

		/// <summary>
		/// Returns whether ModItem.bossBagNPC is greater than 0. Returns false if item is not a modded item.
		/// </summary>
		public static bool IsModBossBag(Item item) {
			return item.ModItem != null && item.ModItem.BossBagNPC > 0;
		}

		/// <summary>
		/// If the item is a modded item and ModItem.bossBagNPC is greater than 0, calls ModItem.OpenBossBag and sets npc to ModItem.bossBagNPC.
		/// </summary>
		public static void OpenBossBag(int type, Player player, ref int npc) {
			ModItem modItem = GetItem(type);

			if (modItem != null && modItem.BossBagNPC > 0) {
				modItem.OpenBossBag(player);
				npc = modItem.BossBagNPC;
			}
		}

		/// <summary>
		/// Calls each GlobalItem.PreOpenVanillaBag hook until one of them returns false. Returns true if all of them returned true.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<string, Player, int, bool>> PreOpenVanillaBag = AddHook(
			new HookList<GlobalItem, Func<string, Player, int, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.PreOpenVanillaBag)),
				//Invocation
				e => (string context, Player player, int arg) => {
					bool result = true;

					foreach (var g in e.Enumerate(globalItemsArray)) {
						result &= g.PreOpenVanillaBag(context, player, arg);
					}

					if (!result) {
						NPCLoader.blockLoot.Clear(); // clear blockloot
						return false;
					}

					return true;
				}
			)
		);

		/// <summary>
		/// Calls all GlobalItem.OpenVanillaBag hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<string, Player, int>> OpenVanillaBag = AddHook(
			new HookList<GlobalItem, Action<string, Player, int>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.OpenVanillaBag)),
				//Invocation
				e => (string context, Player player, int arg) => {
					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.OpenVanillaBag(context, player, arg);
					}
				}
			)
		);

		public delegate bool DelegateReforgePrice(Item item, ref int reforgePrice, ref bool canApplyDiscount);

		/// <summary>
		/// Call all ModItem.ReforgePrice, then GlobalItem.ReforgePrice hooks.
		/// </summary>
		/// <param name="canApplyDiscount"></param>
		/// <returns></returns>
		public static readonly HookList<GlobalItem, DelegateReforgePrice> ReforgePrice = AddHook(
			new HookList<GlobalItem, DelegateReforgePrice>(
				//Method reference
				g => g.ReforgePrice,
				//Invocation
				e => (Item item, ref int reforgePrice, ref bool canApplyDiscount) => {
					bool b = item.ModItem?.ReforgePrice(ref reforgePrice, ref canApplyDiscount) ?? true;

					foreach (var g in e.Enumerate(item.globalItems)) {
						b &= g.ReforgePrice(item, ref reforgePrice, ref canApplyDiscount);
					}

					return b;
				}
			)
		);

		/// <summary>
		/// Calls ModItem.PreReforge, then all GlobalItem.PreReforge hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, bool>> PreReforge = AddHook(
			new HookList<GlobalItem, Func<Item, bool>>(
				//Method reference
				g => g.PreReforge,
				//Invocation
				e => (Item item) => {
					bool b = item.ModItem?.PreReforge() ?? true;

					foreach (var g in e.Enumerate(item.globalItems)) {
						b &= g.PreReforge(item);
					}

					return b;
				}
			)
		);

		/// <summary>
		/// Calls ModItem.PostReforge, then all GlobalItem.PostReforge hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item>> PostReforge = AddHook(
			new HookList<GlobalItem, Action<Item>>(
				//Method reference
				g => g.PostReforge,
				//Invocation
				e => (Item item) => {
					item.ModItem?.PostReforge();

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.PostReforge(item);
					}
				}
			)
		);

		public delegate void DelegateDrawHands(Player player, ref bool drawHands, ref bool drawArms);

		/// <summary>
		/// Calls the item's body equipment texture's DrawHands hook, then all GlobalItem.DrawHands hooks.
		/// "body" is the player's associated body equipment texture.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateDrawHands> DrawHands = AddHook(
			new HookList<GlobalItem, DelegateDrawHands>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.DrawHands)),
				//Invocation
				e => (Player player, ref bool drawHands, ref bool drawArms) => {
					var texture = EquipLoader.GetEquipTexture(EquipType.Body, player.body);

					texture?.DrawHands(ref drawHands, ref drawArms);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.DrawHands(player.body, ref drawHands, ref drawArms);
					}
				}
			)
		);

		public delegate void DelegateDrawHair(Player player, ref bool drawHair, ref bool drawAltHair);
		/// <summary>
		/// Calls the item's head equipment texture's DrawHair hook, then all GlobalItem.DrawHair hooks.
		/// "head" is the player's associated head equipment texture.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateDrawHair> DrawHair = AddHook(
			new HookList<GlobalItem, DelegateDrawHair>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.DrawHair)),
				//Invocation
				e => (Player player, ref bool drawHair, ref bool drawAltHair) => {
					var texture = EquipLoader.GetEquipTexture(EquipType.Head, player.head);

					texture?.DrawHair(ref drawHair, ref drawAltHair);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.DrawHair(player.head, ref drawHair, ref drawAltHair);
					}
				}
			)
		);

		/// <summary>
		/// Calls the item's head equipment texture's DrawHead hook, then all GlobalItem.DrawHead hooks, until one of them returns false. Returns true if none of them return false.
		/// "head" is the player's associated head equipment texture.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Player, bool>> DrawHead = AddHook(
			new HookList<GlobalItem, Func<Player, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.DrawHead)),
				//Invocation
				e => (Player player) => {
					EquipTexture texture = EquipLoader.GetEquipTexture(EquipType.Head, player.head);

					if (texture != null && !texture.DrawHead())
						return false;

					foreach (var g in e.Enumerate(globalItemsArray)) {
						if (!g.DrawHead(player.head))
							return false;
					}

					return true;
				}
			)
		);

		/// <summary>
		/// Calls the item's body equipment texture's DrawBody hook, then all GlobalItem.DrawBody hooks, until one of them returns false. Returns true if none of them return false.
		/// "body" is the player's associated body equipment texture.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Player, bool>> DrawBody = AddHook(
			new HookList<GlobalItem, Func<Player, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.DrawBody)),
				//Invocation
				e => (Player player) => {
					var texture = EquipLoader.GetEquipTexture(EquipType.Body, player.body);

					if (texture != null && !texture.DrawBody())
						return false;

					foreach (var g in e.Enumerate(globalItemsArray)) {
						if (!g.DrawBody(player.body))
							return false;
					}

					return true;
				}
			)
		);

		/// <summary>
		/// Calls the item's leg equipment texture's DrawLegs hook, then the item's shoe equipment texture's DrawLegs hook, then all GlobalItem.DrawLegs hooks, until one of them returns false. Returns true if none of them return false.
		/// "legs" and "shoes" are the player's associated legs and shoes equipment textures.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Player, bool>> DrawLegs = AddHook(
			new HookList<GlobalItem, Func<Player, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.DrawLegs)),
				//Invocation
				e => (Player player) => {
					var texture = EquipLoader.GetEquipTexture(EquipType.Legs, player.legs);

					if (texture != null && !texture.DrawLegs())
						return false;

					texture = EquipLoader.GetEquipTexture(EquipType.Shoes, player.shoe);

					if (texture != null && !texture.DrawLegs())
						return false;

					foreach (var g in e.Enumerate(globalItemsArray)) {
						if (!g.DrawLegs(player.legs, player.shoe))
							return false;
					}

					return true;
				}
			)
		);

		public delegate void DelegateDrawArmorColor(EquipType type, int slot, Player drawPlayer, float shadow, ref Color color, ref int glowMask, ref Color glowMaskColor);

		/// <summary>
		/// Calls the item's equipment texture's DrawArmorColor hook, then all GlobalItem.DrawArmorColor hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateDrawArmorColor> DrawArmorColor = AddHook(
			new HookList<GlobalItem, DelegateDrawArmorColor>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.DrawArmorColor)),
				//Invocation
				e => (EquipType type, int slot, Player drawPlayer, float shadow, ref Color color, ref int glowMask, ref Color glowMaskColor) => {
					var texture = EquipLoader.GetEquipTexture(type, slot);

					texture?.DrawArmorColor(drawPlayer, shadow, ref color, ref glowMask, ref glowMaskColor);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.DrawArmorColor(type, slot, drawPlayer, shadow, ref color, ref glowMask, ref glowMaskColor);
					}
				}
			)
		);

		public delegate void DelegateArmorArmGlowMask(int slot, Player drawPlayer, float shadow, ref int glowMask, ref Color color);

		/// <summary>
		/// Calls the item's body equipment texture's ArmorArmGlowMask hook, then all GlobalItem.ArmorArmGlowMask hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateArmorArmGlowMask> ArmorArmGlowMask = AddHook(
			new HookList<GlobalItem, DelegateArmorArmGlowMask>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.ArmorArmGlowMask)),
				//Invocation
				e => (int slot, Player drawPlayer, float shadow, ref int glowMask, ref Color color) => {
					var texture = EquipLoader.GetEquipTexture(EquipType.Body, slot);

					texture?.ArmorArmGlowMask(drawPlayer, shadow, ref glowMask, ref color);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.ArmorArmGlowMask(slot, drawPlayer, shadow, ref glowMask, ref color);
					}
				}
			)
		);

		/// <summary>s
		/// Returns the wing item that the player is functionally using. If player.wingsLogic has been modified, so no equipped wing can be found to match what the player is using, this creates a new Item object to return.
		/// </summary>
		public static Item GetWing(Player player) {
			//TODO: this doesn't work with wings in modded accessory slots
			Item item = null;

			for (int k = 3; k < 10; k++) {
				if (player.armor[k].wingSlot == player.wingsLogic) {
					item = player.armor[k];
				}
			}

			if (item != null) {
				return item;
			}

			if (player.wingsLogic > 0 && player.wingsLogic < Main.maxWings) {
				item = new Item();
				item.SetDefaults(vanillaWings[player.wingsLogic]);
				return item;
			}

			if (player.wingsLogic >= Main.maxWings) {
				var texture = EquipLoader.GetEquipTexture(EquipType.Wings, player.wingsLogic);

				if (texture?.Item != null)
					return texture.Item.Item;
			}

			return null;
		}

		public delegate void DelegateVerticalWingSpeeds(Player player, ref float ascentWhenFalling, ref float ascentWhenRising, ref float maxCanAscendMultiplier, ref float maxAscentMultiplier, ref float constantAscend);

		/// <summary>
		/// If the player is using wings, this uses the result of GetWing, and calls ModItem.VerticalWingSpeeds then all GlobalItem.VerticalWingSpeeds hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateVerticalWingSpeeds> VerticalWingSpeeds = AddHook(
			new HookList<GlobalItem, DelegateVerticalWingSpeeds>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.VerticalWingSpeeds)),
				//Invocation
				e => (Player player, ref float ascentWhenFalling, ref float ascentWhenRising, ref float maxCanAscendMultiplier, ref float maxAscentMultiplier, ref float constantAscend) => {
					Item item = GetWing(player);

					if (item == null) {
						var texture = EquipLoader.GetEquipTexture(EquipType.Wings, player.wingsLogic);

						texture?.VerticalWingSpeeds(player, ref ascentWhenFalling, ref ascentWhenRising, ref maxCanAscendMultiplier, ref maxAscentMultiplier, ref constantAscend);

						return;
					}

					item.ModItem?.VerticalWingSpeeds(player, ref ascentWhenFalling, ref ascentWhenRising, ref maxCanAscendMultiplier, ref maxAscentMultiplier, ref constantAscend);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.VerticalWingSpeeds(item, player, ref ascentWhenFalling, ref ascentWhenRising, ref maxCanAscendMultiplier, ref maxAscentMultiplier, ref constantAscend);
					}
				}
			)
		);

		/// <summary>
		/// If the player is using wings, this uses the result of GetWing, and calls ModItem.HorizontalWingSpeeds then all GlobalItem.HorizontalWingSpeeds hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Player>> HorizontalWingSpeeds = AddHook(
			new HookList<GlobalItem, Action<Player>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.HorizontalWingSpeeds)),
				//Invocation
				e => (Player player) => {
					Item item = GetWing(player);

					if (item == null) {
						var texture = EquipLoader.GetEquipTexture(EquipType.Wings, player.wingsLogic);

						texture?.HorizontalWingSpeeds(player, ref player.accRunSpeed, ref player.runAcceleration);
						return;
					}

					item.ModItem?.HorizontalWingSpeeds(player, ref player.accRunSpeed, ref player.runAcceleration);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.HorizontalWingSpeeds(item, player, ref player.accRunSpeed, ref player.runAcceleration);
					}
				}
			)
		);

		/// <summary>
		/// If wings can be seen on the player, calls the player's wing's equipment texture's WingUpdate and all GlobalItem.WingUpdate hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Player, bool, bool>> WingUpdate = AddHook(
			new HookList<GlobalItem, Func<Player, bool, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.WingUpdate)),
				//Invocation
				e => (Player player, bool inUse) => {
					if (player.wings <= 0)
						return false;

					var texture = EquipLoader.GetEquipTexture(EquipType.Wings, player.wings);
					bool? retVal = texture?.WingUpdate(player, inUse);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						retVal |= g.WingUpdate(player.wings, player, inUse);
					}

					return retVal ?? false;
				}
			)
		);

		public delegate void DelegateUpdate(Item item, ref float gravity, ref float maxFallSpeed);
		/// <summary>
		/// Calls ModItem.Update, then all GlobalItem.Update hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateUpdate> Update = AddHook(
			new HookList<GlobalItem, DelegateUpdate>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.Update)),
				//Invocation
				e => (Item item, ref float gravity, ref float maxFallSpeed) => {
					item.ModItem?.Update(ref gravity, ref maxFallSpeed);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.Update(item, ref gravity, ref maxFallSpeed);
					}
				}
			)
		);

		/// <summary>
		/// Calls ModItem.CanBurnInLava.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, bool?>> CanBurnInLava = AddHook(new HookList<GlobalItem, Func<Item, bool?>>(
			//Method reference
			typeof(GlobalItem).GetMethod(nameof(GlobalItem.CanBurnInLava)),
			//Invocation
			e => (Item item) => {
				bool? canBurnInLava = null;

				foreach (var g in e.Enumerate(item.globalItems)) {
					switch (g.CanBurnInLava(item)) {
						case null:
							continue;
						case false:
							canBurnInLava = false;
							continue;
						case true:
							return true;
					}
				}

				return canBurnInLava ?? item.ModItem?.CanBurnInLava();
			}
		));

		/// <summary>
		/// Calls ModItem.PostUpdate and all GlobalItem.PostUpdate hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item>> PostUpdate = AddHook(
			new HookList<GlobalItem, Action<Item>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.PostUpdate)),
				//Invocation
				e => (Item item) => {
					item.ModItem?.PostUpdate();

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.PostUpdate(item);
					}
				}
			)
		);

		public delegate void DelegateGrabRange(Item item, Player player, ref int grabRange);

		/// <summary>
		/// Calls ModItem.GrabRange, then all GlobalItem.GrabRange hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegateGrabRange> GrabRange = AddHook(
			new HookList<GlobalItem, DelegateGrabRange>(
				//Method reference
				g => g.GrabRange,
				//Invocation
				e => (Item item, Player player, ref int grabRange) => {
					item.ModItem?.GrabRange(player, ref grabRange);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.GrabRange(item, player, ref grabRange);
					}
				}
			)
		);

		/// <summary>
		/// Calls all GlobalItem.GrabStyle hooks then ModItem.GrabStyle, until one of them returns true. Returns whether any of the hooks returned true.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> GrabStyle = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.GrabStyle,
				//Invocation
				e => (Item item, Player player) => {
					foreach (var g in e.Enumerate(item.globalItems)) {
						if (g.GrabStyle(item, player))
							return true;
					}

					return item.ModItem != null && item.ModItem.GrabStyle(player);
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> CanPickup = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.CanPickup,
				//Invocation
				e => (Item item, Player player) => {
					foreach (var g in e.Enumerate(item.globalItems)) {
						if (!g.CanPickup(item, player))
							return false;
					}

					return item.ModItem?.CanPickup(player) ?? true;
				}
			)
		);

		/// <summary>
		/// Calls all GlobalItem.OnPickup hooks then ModItem.OnPickup, until one of the returns false. Returns true if all of the hooks return true.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> OnPickup = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.OnPickup,
				//Invocation
				e => (Item item, Player player) => {
					foreach (var g in e.Enumerate(item.globalItems)) {
						if (!g.OnPickup(item, player))
							return false;
					}

					return item.ModItem?.OnPickup(player) ?? true;
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, Player, bool>> ItemSpace = AddHook(
			new HookList<GlobalItem, Func<Item, Player, bool>>(
				//Method reference
				g => g.ItemSpace,
				//Invocation
				e => (Item item, Player player) => {
					foreach (var g in e.Enumerate(item.globalItems)) {
						if (g.ItemSpace(item, player))
							return true;
					}

					return item.ModItem?.ItemSpace(player) ?? false;
				}
			)
		);

		/// <summary>
		/// Calls all GlobalItem.GetAlpha hooks then ModItem.GetAlpha, until one of them returns a color, and returns that color. Returns null if all of the hooks return null.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, Color, Color?>> GetAlpha = AddHook(
			new HookList<GlobalItem, Func<Item, Color, Color?>>(
				//Method reference
				g => g.GetAlpha,
				//Invocation
				e => (Item item, Color lightColor) => {
					if (item.IsAir)
						return null;

					foreach (var g in e.Enumerate(item.globalItems)) {
						Color? color = g.GetAlpha(item, lightColor);

						if (color.HasValue)
							return color;
					}

					return item.ModItem?.GetAlpha(lightColor);
				}
			)
		);

		public delegate bool DelegatePreDrawInWorld(Item item, SpriteBatch spriteBatch, Color lightColor, Color alphaColor, ref float rotation, ref float scale, int whoAmI);

		/// <summary>
		/// Returns the "and" operator on the results of ModItem.PreDrawInWorld and all GlobalItem.PreDrawInWorld hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, DelegatePreDrawInWorld> PreDrawInWorld = AddHook(
			new HookList<GlobalItem, DelegatePreDrawInWorld>(
				//Method reference
				g => g.PreDrawInWorld,
				//Invocation
				e => (Item item, SpriteBatch spriteBatch, Color lightColor, Color alphaColor, ref float rotation, ref float scale, int whoAmI) => {
					bool flag = true;

					if (item.ModItem != null)
						flag &= item.ModItem.PreDrawInWorld(spriteBatch, lightColor, alphaColor, ref rotation, ref scale, whoAmI);

					foreach (var g in e.Enumerate(item.globalItems)) {
						flag &= g.PreDrawInWorld(item, spriteBatch, lightColor, alphaColor, ref rotation, ref scale, whoAmI);
					}

					return flag;
				}
			)
		);

		/// <summary>
		/// Calls ModItem.PostDrawInWorld, then all GlobalItem.PostDrawInWorld hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, SpriteBatch, Color, Color, float, float, int>> PostDrawInWorld = AddHook(
			new HookList<GlobalItem, Action<Item, SpriteBatch, Color, Color, float, float, int>>(
				//Method reference
				g => g.PostDrawInWorld,
				//Invocation
				e => (Item item, SpriteBatch spriteBatch, Color lightColor, Color alphaColor, float rotation, float scale, int whoAmI) => {
					item.ModItem?.PostDrawInWorld(spriteBatch, lightColor, alphaColor, rotation, scale, whoAmI);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.PostDrawInWorld(item, spriteBatch, lightColor, alphaColor, rotation, scale, whoAmI);
					}
				}
			)
		);

		/// <summary>
		/// Returns the "and" operator on the results of all GlobalItem.PreDrawInInventory hooks and ModItem.PreDrawInInventory.
		/// </summary>
		public static readonly HookList<GlobalItem, Func<Item, SpriteBatch, Vector2, Rectangle, Color, Color, Vector2, float, bool>> PreDrawInInventory = AddHook(
			new HookList<GlobalItem, Func<Item, SpriteBatch, Vector2, Rectangle, Color, Color, Vector2, float, bool>>(
				//Method reference
				g => g.PreDrawInInventory,
				//Invocation
				e => (Item item, SpriteBatch spriteBatch, Vector2 position, Rectangle frame, Color drawColor, Color itemColor, Vector2 origin, float scale) => {
					bool flag = true;

					foreach (var g in e.Enumerate(item.globalItems)) {
						flag &= g.PreDrawInInventory(item, spriteBatch, position, frame, drawColor, itemColor, origin, scale);
					}

					if (item.ModItem != null)
						flag &= item.ModItem.PreDrawInInventory(spriteBatch, position, frame, drawColor, itemColor, origin, scale);

					return flag;
				}
			)
		);

		/// <summary>
		/// Calls ModItem.PostDrawInInventory, then all GlobalItem.PostDrawInInventory hooks.
		/// </summary>
		public static readonly HookList<GlobalItem, Action<Item, SpriteBatch, Vector2, Rectangle, Color, Color, Vector2, float>> PostDrawInInventory = AddHook(
			new HookList<GlobalItem, Action<Item, SpriteBatch, Vector2, Rectangle, Color, Color, Vector2, float>>(
				//Method reference
				g => g.PostDrawInInventory,
				//Invocation
				e => (Item item, SpriteBatch spriteBatch, Vector2 position, Rectangle frame, Color drawColor, Color itemColor, Vector2 origin, float scale) => {
					item.ModItem?.PostDrawInInventory(spriteBatch, position, frame, drawColor, itemColor, origin, scale);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.PostDrawInInventory(item, spriteBatch, position, frame, drawColor, itemColor, origin, scale);
					}
				}
			)
		);

		public delegate void DelegateHoldoutOfffset(float gravDir, int type, ref Vector2 offset);

		public static readonly HookList<GlobalItem, DelegateHoldoutOfffset> HoldoutOffset = AddHook(
			new HookList<GlobalItem, DelegateHoldoutOfffset>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.HoldoutOffset)),
				//Invocation
				e => (float gravDir, int type, ref Vector2 offset) => {
					ModItem modItem = GetItem(type);

					if (modItem != null) {
						Vector2? modOffset = modItem.HoldoutOffset();

						if (modOffset.HasValue) {
							offset.X = modOffset.Value.X;
							offset.Y += gravDir * modOffset.Value.Y;
						}
					}

					foreach (var g in e.Enumerate(globalItemsArray)) {
						Vector2? modOffset = g.HoldoutOffset(type);

						if (modOffset.HasValue) {
							offset.X = modOffset.Value.X;
							offset.Y = TextureAssets.Item[type].Value.Height / 2f + gravDir * modOffset.Value.Y;
						}
					}
				}
			)
		);

		public delegate void DelegateHoldoutOrigin(Player player, ref Vector2 origin);

		public static readonly HookList<GlobalItem, DelegateHoldoutOrigin> HoldoutOrigin = AddHook(
			new HookList<GlobalItem, DelegateHoldoutOrigin>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.HoldoutOrigin)),
				//Invocation
				e => (Player player, ref Vector2 origin) => {
					Item item = player.inventory[player.selectedItem];
					Vector2 modOrigin = Vector2.Zero;

					if (item.ModItem != null) {
						Vector2? modOrigin2 = item.ModItem.HoldoutOrigin();

						if (modOrigin2.HasValue) {
							modOrigin = modOrigin2.Value;
						}
					}

					foreach (var g in e.Enumerate(item.globalItems)) {
						Vector2? modOrigin2 = g.HoldoutOrigin(item.type);

						if (modOrigin2.HasValue) {
							modOrigin = modOrigin2.Value;
						}
					}

					modOrigin.X *= player.direction;
					modOrigin.Y *= -player.gravDir;
					origin += modOrigin;
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, int, bool>> CanEquipAccessory = AddHook(
			new HookList<GlobalItem, Func<Item, int, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.CanEquipAccessory)),
				//Invocation
				e => (Item item, int slot) => {
					Player player = Main.player[Main.myPlayer];

					if (item.ModItem != null && !item.ModItem.CanEquipAccessory(player, slot))
						return false;

					foreach (var g in e.Enumerate(item.globalItems)) {
						if (!g.CanEquipAccessory(item, player, slot))
							return false;
					}

					return true;
				}
			)
		);

		public delegate void DelegateExtractinatorUse(ref int resultType, ref int resultStack, int extractType);

		public static readonly HookList<GlobalItem, DelegateExtractinatorUse> ExtractinatorUse = AddHook(
			new HookList<GlobalItem, DelegateExtractinatorUse>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.ExtractinatorUse)),
				//Invocation
				e => (ref int resultType, ref int resultStack, int extractType) => {
					GetItem(extractType)?.ExtractinatorUse(ref resultType, ref resultStack);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.ExtractinatorUse(extractType, ref resultType, ref resultStack);
					}
				}
			)
		);

		public delegate void DelegateCaughtFishStack(int type, ref int stack);

		public static readonly HookList<GlobalItem, Action<Item>> CaughtFishStack = AddHook(
			new HookList<GlobalItem, Action<Item>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.CaughtFishStack)),
				//Invocation
				e => (Item item) => {
					item.ModItem?.CaughtFishStack(ref item.stack);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.CaughtFishStack(item.type, ref item.stack);
					}
				}
			)
		);

		public delegate void DelegateIsAnglerQuestAvailable(int itemID, ref bool notAvailable);

		public static readonly HookList<GlobalItem, DelegateIsAnglerQuestAvailable> IsAnglerQuestAvailable = AddHook(
			new HookList<GlobalItem, DelegateIsAnglerQuestAvailable>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.IsAnglerQuestAvailable)),
				//Invocation
				e => (int itemID, ref bool notAvailable) => {
					ModItem modItem = GetItem(itemID);
					if (modItem != null)
						notAvailable |= !modItem.IsAnglerQuestAvailable();

					foreach (var g in e.Enumerate(globalItemsArray)) {
						notAvailable |= !g.IsAnglerQuestAvailable(itemID);
					}
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<int, string>> AnglerChat = AddHook(
			new HookList<GlobalItem, Func<int, string>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.AnglerChat)),
				//Invocation
				e => (int type) => {
					string chat = "";
					string catchLocation = "";

					GetItem(type)?.AnglerQuestChat(ref chat, ref catchLocation);

					foreach (var g in e.Enumerate(globalItemsArray)) {
						g.AnglerChat(type, ref chat, ref catchLocation);
					}

					if (string.IsNullOrEmpty(chat) || string.IsNullOrEmpty(catchLocation))
						return null;

					return chat + "\n\n(" + catchLocation + ")";
				}
			)
		);

		public delegate bool DelegatePreDrawTooltip(Item item, ReadOnlyCollection<TooltipLine> lines, ref int x, ref int y);
		public static readonly HookList<GlobalItem, DelegatePreDrawTooltip> PreDrawTooltip = AddHook(
			new HookList<GlobalItem, DelegatePreDrawTooltip>(
				//Method reference
				g => g.PreDrawTooltip,
				//Invocation
				e => (Item item, ReadOnlyCollection<TooltipLine> lines, ref int x, ref int y) => {
					bool modItemPreDraw = item.ModItem?.PreDrawTooltip(lines, ref x, ref y) ?? true;
					var globalItemPreDraw = new List<bool>();

					foreach (var g in e.Enumerate(item.globalItems)) {
						globalItemPreDraw.Add(g.PreDrawTooltip(item, lines, ref x, ref y));
					}

					return modItemPreDraw && globalItemPreDraw.All(z => z);
				}
			)
		);

		public delegate void DelegatePostDrawTooltip(Item item, ReadOnlyCollection<DrawableTooltipLine> lines);
		public static readonly HookList<GlobalItem, DelegatePostDrawTooltip> PostDrawTooltip = AddHook(
			new HookList<GlobalItem, DelegatePostDrawTooltip>(
				//Method reference
				g => g.PostDrawTooltip,
				//Invocation
				e => (Item item, ReadOnlyCollection<DrawableTooltipLine> lines) => {
					item.ModItem?.PostDrawTooltip(lines);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.PostDrawTooltip(item, lines);
					}
				}
			)
		);

		public delegate bool DelegatePreDrawTooltipLine(Item item, DrawableTooltipLine line, ref int yOffset);
		public static readonly HookList<GlobalItem, DelegatePreDrawTooltipLine> PreDrawTooltipLine = AddHook(
			new HookList<GlobalItem, DelegatePreDrawTooltipLine>(
				//Method reference
				g => g.PreDrawTooltipLine,
				//Invocation
				e => (Item item, DrawableTooltipLine line, ref int yOffset) => {
					bool modItemPreDrawLine = item.ModItem?.PreDrawTooltipLine(line, ref yOffset) ?? true;
					var globalItemPreDrawLine = new List<bool>();

					foreach (var g in e.Enumerate(item.globalItems)) {
						globalItemPreDrawLine.Add(g.PreDrawTooltipLine(item, line, ref yOffset));
					}

					return modItemPreDrawLine && globalItemPreDrawLine.All(x => x);
				}
			)
		);

		public delegate void DelegatePostDrawTooltipLine(Item item, DrawableTooltipLine line);
		public static readonly HookList<GlobalItem, DelegatePostDrawTooltipLine> PostDrawTooltipLine = AddHook(
			new HookList<GlobalItem, DelegatePostDrawTooltipLine>(
				//Method reference
				g => g.PostDrawTooltipLine,
				//Invocation
				e => (Item item, DrawableTooltipLine line) => {
					item.ModItem?.PostDrawTooltipLine(line);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.PostDrawTooltipLine(item, line);
					}
				}
			)
		);

		public delegate List<TooltipLine> DelegateModifyTooltips(Item item, ref int numTooltips, string[] names, ref string[] text, ref bool[] modifier, ref bool[] badModifier, ref int oneDropLogo, out Color?[] overrideColor);

		public static readonly HookList<GlobalItem, DelegateModifyTooltips> ModifyTooltips = AddHook(
			new HookList<GlobalItem, DelegateModifyTooltips>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.ModifyTooltips)),
				//Invocation
				e => (Item item, ref int numTooltips, string[] names, ref string[] text, ref bool[] modifier, ref bool[] badModifier, ref int oneDropLogo, out Color?[] overrideColor) => {
					var tooltips = new List<TooltipLine>();

					for (int k = 0; k < numTooltips; k++) {
						var tooltip = new TooltipLine(names[k], text[k]) {
							isModifier = modifier[k],
							isModifierBad = badModifier[k]
						};

						if (k == oneDropLogo) {
							tooltip.oneDropLogo = true;
						}

						tooltips.Add(tooltip);
					}

					item.ModItem?.ModifyTooltips(tooltips);

					foreach (var g in e.Enumerate(item.globalItems)) {
						g.ModifyTooltips(item, tooltips);
					}

					numTooltips = tooltips.Count;
					text = new string[numTooltips];
					modifier = new bool[numTooltips];
					badModifier = new bool[numTooltips];
					oneDropLogo = -1;
					overrideColor = new Color?[numTooltips];

					for (int k = 0; k < numTooltips; k++) {
						text[k] = tooltips[k].text;
						modifier[k] = tooltips[k].isModifier;
						badModifier[k] = tooltips[k].isModifierBad;

						if (tooltips[k].oneDropLogo) {
							oneDropLogo = k;
						}

						overrideColor[k] = tooltips[k].overrideColor;
					}

					return tooltips;
				}
			)
		);

		public static readonly HookList<GlobalItem, Func<Item, bool>> NeedsModSaving = AddHook(
			new HookList<GlobalItem, Func<Item, bool>>(
				//Method reference
				typeof(GlobalItem).GetMethod(nameof(GlobalItem.NeedsSaving)),
				//Invocation
				e => (Item item) => {
					return item.type != 0 && (item.ModItem != null || item.prefix >= PrefixID.Count || e.Enumerate(item.globalItems).Count(g => g.NeedsSaving(item)) > 0);
				}
			)
		);

		internal static void WriteNetGlobalOrder(BinaryWriter w) {
			w.Write((short)NetGlobals.Length);

			foreach (var globalItem in NetGlobals) {
				w.Write(globalItem.Mod.netID);
				w.Write(globalItem.Name);
			}
		}

		internal static void ReadNetGlobalOrder(BinaryReader r) {
			short n = r.ReadInt16();

			NetGlobals = new GlobalItem[n];

			for (short i = 0; i < n; i++)
				NetGlobals[i] = ModContent.Find<GlobalItem>(ModNet.GetMod(r.ReadInt16()).Name, r.ReadString());
		}

		private static bool HasMethod(Type t, string method, params Type[] args) {
			return t.GetMethod(method, args).DeclaringType != typeof(GlobalItem);
		}

		internal static void VerifyGlobalItem(GlobalItem item) {
			var type = item.GetType();
			int saveMethods = 0;

			if (HasMethod(type, nameof(GlobalItem.NeedsSaving), typeof(Item)))
				saveMethods++;

			if (HasMethod(type, nameof(GlobalItem.Save), typeof(Item)))
				saveMethods++;

			if (HasMethod(type, nameof(GlobalItem.Load), typeof(Item), typeof(TagCompound)))
				saveMethods++;

			if (saveMethods > 0 && saveMethods < 3)
				throw new Exception($"{type} must override all of ({nameof(GlobalItem.NeedsSaving)}/{nameof(GlobalItem.Save)}/{nameof(GlobalItem.Load)}) or none.");

			int netMethods = 0;

			if (HasMethod(type, nameof(GlobalItem.NetSend), typeof(Item), typeof(BinaryWriter)))
				netMethods++;

			if (HasMethod(type, nameof(GlobalItem.NetReceive), typeof(Item), typeof(BinaryReader)))
				netMethods++;

			if (netMethods == 1)
				throw new Exception($"{type} must override both of ({nameof(GlobalItem.NetSend)}/{nameof(GlobalItem.NetReceive)}) or none.");

			bool hasInstanceFields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
				.Any(f => f.DeclaringType.IsSubclassOf(typeof(GlobalItem)));

			if (hasInstanceFields) {
				if (!item.InstancePerEntity)
					throw new Exception($"{type} has instance fields but does not set InstancePerEntity to true. Either use static fields, or per instance globals");

				if (!HasMethod(type, "Clone", typeof(Item), typeof(Item)))
					throw new Exception($"{type} has InstancePerEntity but does not override Clone(Item, Item)");
			}
		}
	}
}
