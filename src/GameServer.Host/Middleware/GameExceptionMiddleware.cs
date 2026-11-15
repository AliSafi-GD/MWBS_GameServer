using GameServer.Abstractions;
using GameServer.Core;

namespace GameServer.Host.Middleware;

public static class GameExceptionMiddleware
{
    /// <summary>Turns GameException (thrown by validation, stores and modules) into clean JSON errors.</summary>
    public static IApplicationBuilder UseGameExceptionHandler(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (GameException ex) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusFor(ex.Code);
                await context.Response.WriteAsJsonAsync(new { error = ex.Code, message = ex.Message });
            }
        });

    /// <summary>The HTTP status that goes with a GameException code. Unknown codes are 400.</summary>
    public static int StatusFor(string code) => code switch
    {
        "username_taken" => StatusCodes.Status409Conflict,
        "not_found" => StatusCodes.Status404NotFound,
        "identity_in_use" or "provider_already_linked" or "insufficient_funds" or "insufficient_items" => StatusCodes.Status409Conflict,
        "last_login_method" => StatusCodes.Status403Forbidden,
        "unknown_provider" or "unknown_leaderboard" or "unknown_tournament" => StatusCodes.Status404NotFound,
        "tournament_not_active" or "tournament_not_ended" or "limit_reached" or "reward_unclaimed"
            or "expired" or "no_score" or "no_reward" => StatusCodes.Status409Conflict,
        "not_joined" => StatusCodes.Status403Forbidden,
        "invalid_credential" or "invalid_code" or "invalid_refresh_token" => StatusCodes.Status401Unauthorized,
        "too_soon" or "rate_limited" => StatusCodes.Status429TooManyRequests,
        "group_not_found" or "party_not_found" => StatusCodes.Status404NotFound,
        "muted" or "not_a_member" or "not_group_admin" or "not_group_owner" or "insufficient_rank" or "banned_from_group"
            or "not_party_leader" or "not_invited" or "not_friends" => StatusCodes.Status403Forbidden,
        "already_member" or "already_requested" or "already_in_party" or "party_full" or "group_full" or "friend_limit"
            or "too_many_invites" or "group_name_taken" or "group_limit_reached" or "owner_cannot_leave" or "blocked"
            or "party_requires_connection" or "not_online" or "not_in_channel" or "too_many_channels" => StatusCodes.Status409Conflict,
        "product_not_found" or "goal_not_found" or "daily_not_configured" => StatusCodes.Status404NotFound,
        "goal_not_complete" or "already_claimed" or "daily_already_claimed" => StatusCodes.Status409Conflict,
        "product_unavailable" or "purchase_limit_reached" => StatusCodes.Status409Conflict,
        "unknown_match_mode" or "match_not_found" or "match_handler_not_found" => StatusCodes.Status404NotFound,
        "match_create_forbidden" or "kicked_from_match" => StatusCodes.Status403Forbidden,
        "match_full" or "already_queued" or "too_many_tickets" or "too_many_matches" or "matchmaking_disabled"
            or "party_too_large" => StatusCodes.Status409Conflict,
        "match_transports_disabled" or "transport_not_enabled" => StatusCodes.Status503ServiceUnavailable,
        "provider_unavailable" => StatusCodes.Status502BadGateway,
        "sms_not_configured" => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status400BadRequest
    };
}
