using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboEvents.Database;

/// <summary>
/// A winner and their prize, written before anything is paid (<see cref="PENDING"/>), so a payout a
/// restart broke off is finished, once, when the plugin starts again. The prize is a copy of the
/// set as it was. Each step of the payout is marked as it is done: the currencies under
/// <see cref="OperationId"/> (the wallet pays an operation once), the badge (a player has it or not),
/// and the furni, which has no such receipt: a payout broken off while granting furni is
/// <see cref="UNSURE"/>, for staff to check, rather than paid twice.
/// </summary>
[Table("winners")]
[Index(nameof(Status))]
[Index(nameof(PlayerId), nameof(CreatedAt))]
[Index(nameof(EventId))]
[Index(nameof(CreatedAt))]
public class WinnerEntity
{
    public const string PENDING = "pending";
    public const string PAID = "paid";
    public const string FAILED = "failed";
    public const string UNSURE = "unsure";

    public const int MAX_ERROR = 255;

    [Key]
    [Column("id")]
    public long Id { get; set; }

    [Column("event_id")]
    public long? EventId { get; set; }

    [Column("room_id")]
    public int RoomId { get; set; }

    [Column("player_id")]
    public int PlayerId { get; set; }

    [Column("player_name")]
    [MaxLength(HostedEventEntity.MAX_NAME)]
    public string PlayerName { get; set; } = string.Empty;

    [Column("awarded_by_id")]
    public int AwardedById { get; set; }

    [Column("awarded_by")]
    [MaxLength(HostedEventEntity.MAX_NAME)]
    public string AwardedBy { get; set; } = string.Empty;

    [Column("prize_name")]
    [MaxLength(PrizeSetEntity.MAX_NAME)]
    public string PrizeName { get; set; } = string.Empty;

    [Column("currencies")]
    [MaxLength(PrizeSetEntity.MAX_CURRENCIES)]
    public string Currencies { get; set; } = string.Empty;

    [Column("badge_code")]
    [MaxLength(PrizeSetEntity.MAX_BADGE)]
    public string? BadgeCode { get; set; }

    [Column("furni_name")]
    [MaxLength(PrizeSetEntity.MAX_FURNI)]
    public string? FurniName { get; set; }

    [Column("furni_count")]
    public int FurniCount { get; set; }

    /// <summary>The wallet operation the currencies are paid (and, on a failed payout, taken back) under.</summary>
    [Column("operation_id")]
    [MaxLength(64)]
    public string OperationId { get; set; } = string.Empty;

    [Column("status")]
    [MaxLength(16)]
    public string Status { get; set; } = PENDING;

    [Column("currencies_paid")]
    public bool CurrenciesPaid { get; set; }

    /// <summary>The badge was given by this payout (not held before), so a failed payout takes it back.</summary>
    [Column("badge_given")]
    public bool BadgeGiven { get; set; }

    [Column("badge_done")]
    public bool BadgeDone { get; set; }

    /// <summary>Furni granting began; with <see cref="FurniGiven"/> short of the count after a restart, the payout is unsure.</summary>
    [Column("furni_started")]
    public bool FurniStarted { get; set; }

    [Column("furni_given")]
    public int FurniGiven { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("paid_at")]
    public DateTime? PaidAt { get; set; }

    [Column("error")]
    [MaxLength(MAX_ERROR)]
    public string? Error { get; set; }
}
