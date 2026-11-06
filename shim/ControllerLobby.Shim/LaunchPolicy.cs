using System;
using ControllerLobby.Transaction;

// Shim slice S7: the launch policy (LAUNCH-TRANSACTION r5 section 15). The policy is decided once, before the lobby
// opens. The only production policy is esc-only: every lobby result, a valid sealed confirm included, ends with no game
// and no target-factory call. The routed branch runs the accepted transaction core (S6) with the exact section 4 gate;
// it is reachable only through a LaunchSeam, which the offline checks construct and the production entry point never
// passes.
namespace ControllerLobby.Shim
{
    internal enum LaunchPolicy { EscOnly, Routed }

    /// <summary>Check-only inputs of the routed branch. No CLI, environment or configuration value creates one.</summary>
    internal sealed class LaunchSeam
    {
        public LaunchPolicy Policy { get; init; }
        /// <summary>Builds the target factory from the sidecar's resolved target and the unmodified command-line tail.</summary>
        public Func<string, string, ITargetFactory> FactoryFor { get; init; }
        public Func<bool> Helper { get; init; }
        public Func<bool> Route { get; init; }
        public ICompletion Completion { get; init; }
        public TimeSpan TargetBound { get; init; } = TimeSpan.FromSeconds(10);
        public TimeSpan CompletionBound { get; init; } = TimeSpan.FromSeconds(5);

        /// <summary>S8: the routed target factory. lpApplicationName = the resolved target; lpCommandLine = the quoted
        /// target followed by the unmodified tail (LAUNCHER-SHIM section 3); working directory = the target's folder, as
        /// before S8. Used only by a seam's FactoryFor.</summary>
        public static ITargetFactory NativeTarget(string resolvedTarget, string tail) =>
            NativeTargetFactory.WithCompleteCommandLine(resolvedTarget, ShimRules.BuildChildCommandLine(resolvedTarget, tail),
                System.IO.Path.GetDirectoryName(resolvedTarget)); // guard:target-folder-cwd
    }

    /// <summary>Builds the seam's factory inside the core's creation step, so a preparation exception becomes a logged No
    /// game (exit 5, evidence first) instead of escaping the shim .</summary>
    internal sealed class DeferredFactory : ITargetFactory
    {
        private readonly Func<ITargetFactory> make;
        public DeferredFactory(Func<ITargetFactory> make) { this.make = make; }
        public ISuspendedTarget CreateSuspendedInJob(System.Collections.Generic.List<string> failures)
        {
            var inner = make() ?? throw new InvalidOperationException("no target factory");
            return inner.CreateSuspendedInJob(failures);
        }
    }
}
