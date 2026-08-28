using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Notifications.Grimmory
{
    public class GrimmorySettingsValidator : AbstractValidator<GrimmorySettings>
    {
        public GrimmorySettingsValidator()
        {
            RuleFor(c => c.Url).NotEmpty().WithMessage("URL cannot be empty");
            RuleFor(c => c.Url).IsValidUrl().When(c => c.Url.IsNotNullOrWhiteSpace());
            RuleFor(c => c.Username).NotEmpty().WithMessage("Username is required");
            RuleFor(c => c.Password).NotEmpty().WithMessage("Password is required");
            RuleFor(c => c.LibraryId).GreaterThan(0).WithMessage("Library is required");
        }
    }

    public class GrimmorySettings : IProviderConfig
    {
        private static readonly GrimmorySettingsValidator Validator = new GrimmorySettingsValidator();

        [FieldDefinition(0, Label = "URL", HelpText = "Grimmory URL, including http(s):// and port, e.g. http://grimmory:6060")]
        public string Url { get; set; }

        [FieldDefinition(1, Label = "Username", Privacy = PrivacyLevel.UserName, HelpText = "Grimmory user with permission to manage libraries")]
        public string Username { get; set; }

        [FieldDefinition(2, Label = "Password", Type = FieldType.Password, Privacy = PrivacyLevel.Password)]
        public string Password { get; set; }

        [FieldDefinition(3, Label = "Library", Type = FieldType.Select, SelectOptionsProviderAction = "getLibraries", HelpText = "Grimmory library to refresh when Chaptarr imports, renames or deletes ebook files")]
        public int LibraryId { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
