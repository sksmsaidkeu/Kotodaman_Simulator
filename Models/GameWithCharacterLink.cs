namespace KotodamanWordFinder.Models;

public sealed record GameWithCharacterLink(
    string Url,
    string NameHint,
    string GroupHint = "",
    string SubRating = "",
    string LeaderRating = "",
    bool? IsCollaboration = null,
    string AttributeHint = "",
    string LettersHint = "",
    bool RequiresRecentSixStarValidation = false);
