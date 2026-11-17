namespace NoodlesMcDoodles.Core
{
    /// <summary>
    /// The <c>noodles status|enable|disable</c> admin command (doc 02, Hook 4 / P4). Not a Harmony patch —
    /// vanilla's own <c>Terminal.ConsoleCommand</c> extension point, so it never goes through
    /// <see cref="PatchRegistry"/>; <see cref="Init"/> is called directly from <see cref="NoodlesPlugin"/>.
    ///
    /// <b>Why this exact constructor call, not the more obvious one</b> (doc 02 Hook 4's own "Additional
    /// finding", not raised by either red team but found while re-deriving this hook): <c>ConsoleCommand</c>
    /// has two constructor overloads — only the <c>ConsoleEventFailable</c> (object-returning) one folds
    /// <c>onlyAdmin</c> into <c>OnlyServer</c> (confirmed both decompiles: <c>OnlyServer = onlyServer ||
    /// onlyAdmin;</c> vs. the <c>ConsoleEvent</c> (void-returning) overload's plain <c>OnlyServer =
    /// onlyServer;</c>). A void-returning lambda (this one — a bare <c>switch</c>, no return value) can only
    /// bind to the SECOND overload, so <c>onlyAdmin</c> alone would be silently inert: <c>OnlyAdmin</c> is
    /// written by both constructors but never READ anywhere in either decompile — the actual gate is
    /// <c>onlyServer: true</c> (checked by <c>Terminal.IsValid()</c>) combined with <c>remoteCommand: true</c>
    /// (so <c>Terminal.TryRunCommand</c> forwards a non-local call to <c>ZNet.RemoteCommand</c> ->
    /// <c>RPC_RemoteCommand</c>), whose real authorization is vanilla's own
    /// <c>ListContainsId(m_adminList, rpc.GetSocket().GetHostName())</c> check (SERVER:81925-81934) — NOT
    /// anything <c>Terminal.ConsoleCommand</c> does itself. Confirmed this is vanilla's actual convention:
    /// every <c>remoteCommand: true</c> registration in the decompile pairs it with an explicit
    /// <c>onlyServer: true</c>; none pair it with <c>onlyAdmin: true</c>.
    ///
    /// Net effect: typed on the server itself (or by a local admin with shell access, via RCON-less stdin —
    /// out of scope here), the command runs directly (<c>IsValid()</c> true because <c>ZNet.IsServer()</c> is
    /// true there). Typed in a REMOTE client's own in-game chat console, <c>IsValid()</c> is false (that
    /// client is not the server) so <c>TryRunCommand</c> forwards it through <c>ZNet.RemoteCommand</c> to
    /// <c>RPC_RemoteCommand</c>, which only executes it if that client's Steam/host id is in the server's own
    /// admin list — exactly vanilla's real remote-admin pattern, matching every other <c>remoteCommand: true</c>
    /// command in the game.
    /// </summary>
    internal static class AdminCommand
    {
        public static void Init()
        {
            new Terminal.ConsoleCommand(
                "noodles",
                "NoodlesMcDoodles admin control: status | enable | disable | clear <feature>",
                (Terminal.ConsoleEventArgs args) =>
                {
                    if (args.Length < 2)
                    {
                        args.Context.AddString(KillSwitch.StatusLine());
                        return;
                    }

                    switch (args[1].ToLower())
                    {
                        case "status":
                            args.Context.AddString(KillSwitch.StatusLine());
                            break;
                        case "enable":
                            KillSwitch.SetEnabled(true, "admin console (noodles enable)");
                            args.Context.AddString(KillSwitch.StatusLine());
                            break;
                        case "disable":
                            KillSwitch.SetEnabled(false, "admin console (noodles disable)");
                            args.Context.AddString(KillSwitch.StatusLine());
                            break;
                        case "clear":
                            HandleClear(args);
                            break;
                        default:
                            args.Context.AddString("usage: noodles status | enable | disable | clear <feature>");
                            break;
                    }
                },
                isCheat: false,
                isNetwork: false,
                onlyServer: true,   // explicit: do NOT rely on onlyAdmin folding (see class remarks).
                isSecret: false,
                allowInDevBuild: false,
                hideBehindDevCommands: false,
                optionsFetcher: null,
                alwaysRefreshTabOptions: false,
                remoteCommand: true, // forwards to RPC_RemoteCommand when typed by a non-server client.
                onlyAdmin: false);   // dead weight on this overload — the real gate is RPC_RemoteCommand's own admin-list check.
        }

        /// <summary>
        /// WP1: <c>ClearForcedOff()</c> already existed on <see cref="FeatureFlag"/> with zero call sites — this
        /// is its first caller. Clears a per-feature force-off (from <see cref="PatchRegistry"/>, from
        /// <see cref="FeatureStandDown"/>'s quarantine trip, or from the feature's own code) without a restart;
        /// never touches <see cref="KillSwitch"/> — that's still "noodles enable".
        /// </summary>
        private static void HandleClear(Terminal.ConsoleEventArgs args)
        {
            if (args.Length < 3)
            {
                args.Context.AddString("usage: noodles clear <feature>");
                return;
            }

            string featureName = args[2];
            FeatureFlag flag = Features.Find(featureName);
            if (flag == null)
            {
                args.Context.AddString($"noodles clear: unknown feature '{featureName}' (see 'noodles status' for names).");
                return;
            }

            bool wasForcedOff = flag.IsForcedOff;
            flag.ClearForcedOff();
            NoodlesLog.Notice($"feature '{featureName}' force-off cleared via admin console (noodles clear) " +
                (wasForcedOff ? "(was forced off)." : "(was not forced off; no-op)."));
            args.Context.AddString(KillSwitch.StatusLine());
        }
    }
}
