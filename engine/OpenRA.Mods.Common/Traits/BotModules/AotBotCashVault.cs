#region Copyright & License Information
/*
 * Age of Tiberium mod — custom trait.
 * A per-player cash vault the Age-funding bot moves money into and out of. The point is save-load
 * determinism: bot code (AotAgePowerBotModule) used to drain the player's real Cash directly via
 * TakeCash to reserve for the next Age. Cash is a [Sync] field, and bot modules are suppressed while
 * a save is reloading (the reload replays recorded orders with the bots turned off), so that drain
 * never happened on load — the reserved money reappeared, every bot's economy diverged, and the
 * reload went out of sync within a couple of thousand frames.
 *
 * The fund now keeps the same real-cash behaviour (money genuinely leaves the spendable account, so
 * the production queue cannot touch it) but performs the movement through a RECORDED order handled
 * here. Orders are part of the save/replay stream, so the exact same cash history is reproduced on
 * reload and the game stays in sync. The reserve target is absolute, so the handler is idempotent
 * and self-correcting even if an order is missed.
 */
#endregion

using OpenRA.Traits;

namespace OpenRA.Mods.Common.Traits
{
	[Desc("Holds cash a bot has reserved for a later purchase, moved in/out via a recorded order so it",
		"survives a save-load replay. Added to the player actor; only bot-driven players ever fill it.")]
	public class AotBotCashVaultInfo : TraitInfo, Requires<PlayerResourcesInfo>
	{
		public override object Create(ActorInitializer init) { return new AotBotCashVault(init.Self); }
	}

	public class AotBotCashVault : INotifyCreated, IResolveOrder, ISync
	{
		public const string OrderName = "AotBotCashVault";

		// The vaulted amount. Synced so it folds into the sync hash exactly like the Cash account it is
		// taken from (PlayerResources marks Cash the same way); rebuilt on reload by replaying the
		// recorded vault orders from frame 0.
		[VerifySync]
		public int Stored { get; private set; }

		public Actor Self { get; }

		PlayerResources playerResources;

		public AotBotCashVault(Actor self)
		{
			Self = self;
		}

		void INotifyCreated.Created(Actor self)
		{
			playerResources = self.Trait<PlayerResources>();
		}

		// ExtraData is the ABSOLUTE amount that should be vaulted. Move real cash toward it: take from
		// the account to grow the vault, give back to shrink it. Clamped to what is actually available,
		// which is itself deterministic, so play and reload apply identical movements.
		void IResolveOrder.ResolveOrder(Actor self, Order order)
		{
			if (order.OrderString != OrderName)
				return;

			var target = (int)order.ExtraData;
			if (target < 0)
				target = 0;

			if (target > Stored)
			{
				var want = target - Stored;
				var available = playerResources.GetCashAndResources();
				var take = want < available ? want : available;
				if (take > 0 && playerResources.TakeCash(take))
					Stored += take;
			}
			else if (target < Stored)
			{
				var give = Stored - target;
				playerResources.GiveCash(give);
				Stored -= give;
			}
		}
	}
}
