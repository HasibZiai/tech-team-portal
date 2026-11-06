namespace PcServ.Core.Personality;

/// <summary>Places in the app where the Personality Core can comment.</summary>
public enum QuipMoment
{
    LoginSubtitle,
    Welcome,
    Saved,
    Deleted,
    EmptyList,
    AccessDenied,
    NotFound,
    Footer,
    Setup,
    PasswordChange,
}

/// <summary>
/// Lines for the optional "Personality Core", a passive-aggressive testing-facility AI tone
/// in the spirit of GLaDOS from Portal. Every moment also has a plain, professional line
/// that is used when the Personality Core is switched off in Settings.
/// </summary>
public static class Quips
{
    private static readonly Dictionary<QuipMoment, string> Plain = new()
    {
        [QuipMoment.LoginSubtitle] = "Sign in to continue.",
        [QuipMoment.Welcome] = "Welcome back.",
        [QuipMoment.Saved] = "Saved.",
        [QuipMoment.Deleted] = "Deleted.",
        [QuipMoment.EmptyList] = "Nothing here yet.",
        [QuipMoment.AccessDenied] = "You don't have access to this page. Ask a manager if you need it.",
        [QuipMoment.NotFound] = "That page doesn't exist.",
        [QuipMoment.Footer] = "Tech Team Portal",
        [QuipMoment.Setup] = "Create the first administrator account to get started.",
        [QuipMoment.PasswordChange] = "Please choose a new password before continuing.",
    };

    private static readonly Dictionary<QuipMoment, string[]> Personality = new()
    {
        [QuipMoment.LoginSubtitle] =
        [
            "Please enter your credentials. Your cooperation is being noted.",
            "Oh. It's you. Sign in, then.",
            "Authentication is mandatory. Enthusiasm is optional, but encouraged.",
        ],
        [QuipMoment.Welcome] =
        [
            "Welcome back. The Chromebooks have not fixed themselves while you were away. I checked.",
            "Hello again. Your productivity metrics are... present.",
            "Welcome to the Tech Team Portal. Testing will begin shortly. By 'testing' I mean 'your workday'.",
            "Good news: you're logged in. The bad news can wait until you open the repair queue.",
            "You're here. Statistically, that's the hardest part of your day. Statistically.",
        ],
        [QuipMoment.Saved] =
        [
            "Saved. I'd say good job, but let's not set a precedent.",
            "Your changes have been recorded. Permanently. Just like everything else you do.",
            "Saved successfully. Even I'm a little surprised.",
            "Saved. Science thanks you for your contribution.",
        ],
        [QuipMoment.Deleted] =
        [
            "Deleted. It's in a better place now. The audit log.",
            "Gone. Don't worry, I remember it, even if no one else does.",
            "Deleted. I'll add it to the list of things you've gotten rid of.",
        ],
        [QuipMoment.EmptyList] =
        [
            "There's nothing here. Much like the promise of cake.",
            "Empty. Pristine. Ready for you to fill it with problems.",
            "No records found. Enjoy this moment. It will not last.",
            "Nothing here. The cake is a lie, but this empty list is completely real.",
        ],
        [QuipMoment.AccessDenied] =
        [
            "This area is restricted. Please return to your assigned testing track.",
            "You don't have clearance for this. I'm as disappointed as you are. Slightly less, actually.",
        ],
        [QuipMoment.NotFound] =
        [
            "This page does not exist. Please do not attempt to create it by staring at the address bar.",
            "404: page not found. It was never here. You imagined it. That's what I'll say in the report.",
            "This page doesn't exist. Neither does the cake. The cake is a lie.",
        ],
        [QuipMoment.Footer] =
        [
            "Tech Team Portal. All tickets will be resolved. Eventually.",
            "Tech Team Portal. For science. And for e-waste compliance.",
            "Tech Team Portal. Now with 40% more passive aggression.",
            "Tech Team Portal. The cake is a lie. The repair queue is not.",
        ],
        [QuipMoment.Setup] =
        [
            "Hello. I appear to have been activated. Please create an administrator before I start making decisions on my own.",
        ],
        [QuipMoment.PasswordChange] =
        [
            "Your temporary password was temporary. That's what the word means. Please choose a new one.",
        ],
    };

    /// <summary>Returns a line for the moment: personality flavored when enabled, plain otherwise.</summary>
    public static string For(QuipMoment moment, bool personalityEnabled)
    {
        if (!personalityEnabled || !Personality.TryGetValue(moment, out var lines) || lines.Length == 0)
        {
            return Plain[moment];
        }

        return lines[Random.Shared.Next(lines.Length)];
    }

    /// <summary>The plain, professional line for a moment.</summary>
    public static string PlainFor(QuipMoment moment) => Plain[moment];
}
