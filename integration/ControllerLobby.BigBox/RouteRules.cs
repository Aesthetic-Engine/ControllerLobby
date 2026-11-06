namespace ControllerLobby.BigBox;

public enum Route { LegacyDiagnostic, GoldenEye, Turtles, Refuse }
public static class RouteRules
{
    public static Route Decide(string kind,string[] args,string content,string romDirectory)
    {
        // Only these exact argument vectors are allowed to replace an existing launch.
        // A request naming the test game with additional/changed options fails closed.
        var comparer=StringComparer.OrdinalIgnoreCase;
        if(kind=="goldeneye")
        {
            string[] expected=["-L",@"cores\mupen64plus_next_libretro.dll","-f",content];
            if(args.SequenceEqual(expected,comparer))return Route.GoldenEye; // guard:exact-goldeneye
            return args.Any(a=>a.Equals(content,StringComparison.OrdinalIgnoreCase)||a.Contains("GoldenEye",StringComparison.OrdinalIgnoreCase))?Route.Refuse:Route.LegacyDiagnostic;
        }
        if(kind=="turtles")
        {
            string[] expected=["-artwork_crop","-skip_gameinfo","-waitvsync","-nofilter","-keyboardprovider","dinput","-rompath",romDirectory,"tmnt2"];
            if(args.SequenceEqual(expected,comparer))return Route.Turtles; // guard:exact-turtles
            return args.Any(a=>a.Contains("tmnt2",StringComparison.OrdinalIgnoreCase))?Route.Refuse:Route.LegacyDiagnostic;
        }
        return Route.Refuse;
    }
}
