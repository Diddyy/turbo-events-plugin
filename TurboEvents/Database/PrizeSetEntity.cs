using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace TurboEvents.Database;

/// <summary>
/// A prize <c>:winner</c> pays by its name: amounts of the hotel's currencies, a badge and furni,
/// any of them. A winner keeps a copy of what it was when they won.
/// </summary>
[Table("prize_sets")]
[Index(nameof(Name), IsUnique = true)]
public class PrizeSetEntity
{
    public const int MAX_NAME = 32;
    public const int MAX_CURRENCIES = 255;
    public const int MAX_BADGE = 64;
    public const int MAX_FURNI = 100;

    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("name")]
    [MaxLength(MAX_NAME)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The hotel's currency names and amounts: <c>credits:100, duckets:50</c>.</summary>
    [Column("currencies")]
    [MaxLength(MAX_CURRENCIES)]
    public string Currencies { get; set; } = string.Empty;

    [Column("badge_code")]
    [MaxLength(MAX_BADGE)]
    public string? BadgeCode { get; set; }

    /// <summary>A furni definition's name, as <c>:giveitem</c> takes it.</summary>
    [Column("furni_name")]
    [MaxLength(MAX_FURNI)]
    public string? FurniName { get; set; }

    [Column("furni_count")]
    public int FurniCount { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
