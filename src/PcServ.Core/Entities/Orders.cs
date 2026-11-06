using System.ComponentModel.DataAnnotations;

namespace PcServ.Core.Entities;

/// <summary>Where an order is. Declared in the order an order normally moves through them.</summary>
public enum OrderStatus
{
    Requested,
    SentToContact,
    Ordered,
    Shipped,
    Received,
    Cancelled,
}

/// <summary>The wording for order stages, in one place so an organization can change it easily.</summary>
public static class OrderStages
{
    /// <summary>The normal path, without Cancelled. Shipped can be skipped.</summary>
    public static readonly IReadOnlyList<OrderStatus> Path =
        [OrderStatus.Requested, OrderStatus.SentToContact, OrderStatus.Ordered, OrderStatus.Shipped, OrderStatus.Received];

    /// <summary>The stage's name. The sent stage names the person it went to, so give their name when it's known.</summary>
    public static string Name(OrderStatus status, string? contact = null) => status switch
    {
        OrderStatus.Requested => "Requested",
        OrderStatus.SentToContact => string.IsNullOrWhiteSpace(contact) ? "Sent to contact" : $"Sent to {contact.Trim()}",
        OrderStatus.Ordered => "Ordered",
        OrderStatus.Shipped => "Shipped",
        OrderStatus.Received => "Received",
        OrderStatus.Cancelled => "Cancelled",
        _ => status.ToString(),
    };

    public static string Description(OrderStatus status) => status switch
    {
        OrderStatus.Requested => "Recommended, and not yet sent on.",
        OrderStatus.SentToContact => "A quote was created and sent to the person handling the order.",
        OrderStatus.Ordered => "The order has been placed with the vendor.",
        OrderStatus.Shipped => "The vendor has sent it. This stage is optional.",
        OrderStatus.Received => "It's at the site.",
        OrderStatus.Cancelled => "It won't be going ahead.",
        _ => "",
    };

    /// <summary>Whether an order is still on its way. Finished orders drop off the default list and the maps.</summary>
    public static bool IsOpen(OrderStatus status) => status is not (OrderStatus.Received or OrderStatus.Cancelled);

    /// <summary>The usual next stage, or null if the order is finished.</summary>
    public static OrderStatus? Next(OrderStatus status) => status switch
    {
        OrderStatus.Requested => OrderStatus.SentToContact,
        OrderStatus.SentToContact => OrderStatus.Ordered,
        OrderStatus.Ordered => OrderStatus.Shipped,
        OrderStatus.Shipped => OrderStatus.Received,
        _ => null,
    };
}

/// <summary>A vendor on the approved list. Never deleted, so past orders keep their vendor: it's marked as no longer approved instead.</summary>
public class Vendor : Entity
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = "";

    [MaxLength(300), Url]
    public string? Website { get; set; }

    [MaxLength(40)]
    public string? Phone { get; set; }

    [MaxLength(1000)]
    public string? Notes { get; set; }

    /// <summary>Whether people can choose this vendor for new orders.</summary>
    public bool IsApproved { get; set; } = true;
}

public class Order : Entity
{
    [Required, MaxLength(200)]
    public string Item { get; set; } = "";

    public int Quantity { get; set; } = 1;

    public int SiteId { get; set; }
    public Site? Site { get; set; }

    /// <summary>The staff member it's for, if any.</summary>
    [MaxLength(120)]
    public string? RequestedFor { get; set; }

    [MaxLength(60)]
    public string? Room { get; set; }

    /// <summary>The person handling the order. A name, picked from the site's staff list or typed.</summary>
    [MaxLength(120)]
    public string? Contact { get; set; }

    /// <summary>An approved vendor. Null means <see cref="OtherVendor"/> is used.</summary>
    public int? VendorId { get; set; }
    public Vendor? Vendor { get; set; }

    /// <summary>A vendor not on the approved list, such as a catalog or contractor.</summary>
    [MaxLength(120)]
    public string? OtherVendor { get; set; }

    [MaxLength(60)]
    public string? QuoteNumber { get; set; }

    [MaxLength(100)]
    public string? TrackingNumber { get; set; }

    [MaxLength(2000)]
    public string? Notes { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Requested;

    /// <summary>When it reached its current stage.</summary>
    public DateOnly StatusDate { get; set; }

    /// <summary>Its pin on a site map, if it has one.</summary>
    public int? MapFeatureId { get; set; }
    public MapFeature? MapFeature { get; set; }

    public List<OrderStatusChange> History { get; set; } = [];
}

/// <summary>One step in an order's history. The date can be earlier than the entry, for updates made after the fact.</summary>
public class OrderStatusChange : Entity
{
    public int OrderId { get; set; }
    public Order? Order { get; set; }

    public OrderStatus Status { get; set; }

    public DateOnly Date { get; set; }

    /// <summary>Who it was sent to at the time, for the sent stage, even if the order's contact changes later.</summary>
    [MaxLength(120)]
    public string? Contact { get; set; }

    [MaxLength(500)]
    public string? Note { get; set; }
}
