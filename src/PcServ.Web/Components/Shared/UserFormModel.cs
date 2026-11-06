using System.ComponentModel.DataAnnotations;
using PcServ.Core.Security;

namespace PcServ.Web.Components.Shared;

public sealed record UserFormModel
{
    public string? Id { get; set; }

    [Required(ErrorMessage = "Enter the person's name."), MaxLength(120)]
    public string DisplayName { get; set; } = "";

    [Required(ErrorMessage = "Choose a username."), MaxLength(64)]
    [RegularExpression(@"^[A-Za-z0-9._-]+$", ErrorMessage = "Use only letters, numbers, dots, dashes and underscores.")]
    public string UserName { get; set; } = "";

    [Required]
    public string Role { get; set; } = Roles.Tech;
}
