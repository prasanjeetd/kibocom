namespace InventoryHold.Contracts;

/// <summary>Lifecycle state of a hold. Released and Expired are terminal; both restore stock.</summary>
public enum HoldStatus
{
    Active = 0,
    Released = 1,
    Expired = 2
}
