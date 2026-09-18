using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using NetworkLimiter.Contracts.Messages;

namespace NetworkLimiter.App.ViewModels;

/// <summary>
/// Une édition de règle en cours, de l'ouverture à l'enregistrement.
/// </summary>
/// <remarks>
/// <para>
/// Sépare délibérément la <b>saisie</b> — portée par deux <see cref="RateInputViewModel"/>, un
/// par sens — de la <b>cible</b>, qui ne se modifie pas : changer l'application visée d'une règle
/// existante reviendrait à en créer une autre, et masquerait à l'utilisateur qu'il vient de
/// cesser de limiter le premier programme.
/// </para>
/// <para>
/// L'enregistrement est refusé tant qu'une saisie est invalide, et la raison reste affichée
/// pendant la frappe (FR-007). Rien n'est envoyé au service tant que la saisie ne tient pas :
/// une validation côté service existe aussi, mais faire l'aller-retour pour apprendre qu'on a
/// tapé « 5 Go/s » serait une friction inutile.
/// </para>
/// </remarks>
public sealed partial class RuleEditorSessionViewModel : ObservableObject
{
    private RuleEditorSessionViewModel(Guid? ruleId, AppIdentityDto target, bool enabled)
    {
        RuleId = ruleId;
        Target = target;
        _enabled = enabled;

        Download.PropertyChanged += (_, _) => RaiseValidity();
        Upload.PropertyChanged += (_, _) => RaiseValidity();
    }

    /// <summary>Ouvre l'édition d'une règle existante.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="rule"/> est <c>null</c>.</exception>
    public static RuleEditorSessionViewModel Edit(RuleDto rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        var session = new RuleEditorSessionViewModel(rule.Id, rule.Target, rule.Enabled);

        session.Download.LoadFrom(rule.DownloadBytesPerSecond);
        session.Upload.LoadFrom(rule.UploadBytesPerSecond);

        return session;
    }

    /// <summary>Ouvre la création d'une règle pour une application observée.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> est <c>null</c>.</exception>
    public static RuleEditorSessionViewModel Create(ObservedAppDto app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return new RuleEditorSessionViewModel(
            ruleId: null,
            new AppIdentityDto
            {
                ExecutablePath = app.ExecutablePath,
                ExecutableName = app.ExecutableName,
                DisplayName = Path.GetFileNameWithoutExtension(app.ExecutableName),
            },
            enabled: true);
    }

    /// <summary>Identifiant de la règle modifiée, ou <c>null</c> en création.</summary>
    public Guid? RuleId { get; }

    /// <summary>Application visée. Non modifiable une fois l'édition ouverte.</summary>
    public AppIdentityDto Target { get; }

    /// <summary>Titre de la boîte d'édition.</summary>
    public string Title => RuleId is null
        ? $"Limiter {Target.DisplayName}"
        : $"Modifier la limite de {Target.DisplayName}";

    /// <summary>Saisie du plafond descendant.</summary>
    public RateInputViewModel Download { get; } = new();

    /// <summary>Saisie du plafond montant.</summary>
    public RateInputViewModel Upload { get; } = new();

    [ObservableProperty]
    private bool _enabled;

    /// <summary>L'enregistrement est possible.</summary>
    public bool CanSave => Download.IsValid && Upload.IsValid && !BothUnlimited;

    /// <summary>
    /// Une règle sans aucun plafond ne limite rien.
    /// </summary>
    /// <remarks>
    /// Elle serait acceptée par le service — elle est structurellement valide — et n'aurait
    /// aucun effet. L'utilisateur croirait avoir posé une limite. Mieux vaut le dire ici.
    /// </remarks>
    public bool BothUnlimited => Download.Unlimited && Upload.Unlimited;

    /// <summary>Ce qui empêche d'enregistrer, ou <c>null</c>.</summary>
    public string? BlockingMessage
    {
        get
        {
            if (BothUnlimited)
            {
                return "Renseignez au moins un plafond, sinon cette règle ne limitera rien.";
            }

            return Download.ValidationMessage ?? Upload.ValidationMessage;
        }
    }

    /// <summary>Compose la règle à envoyer au service.</summary>
    /// <exception cref="InvalidOperationException">La saisie n'est pas enregistrable.</exception>
    public UpsertRulePayload ToPayload()
    {
        if (!CanSave)
        {
            throw new InvalidOperationException(
                BlockingMessage ?? "La saisie n'est pas enregistrable.");
        }

        return new UpsertRulePayload
        {
            RuleId = RuleId,
            Rule = new RuleDto
            {
                Id = RuleId ?? Guid.NewGuid(),
                Target = Target,
                DownloadBytesPerSecond = Download.BytesPerSecond,
                UploadBytesPerSecond = Upload.BytesPerSecond,
                Enabled = Enabled,
                ExemptFromGlobal = false,
            },
        };
    }

    partial void OnEnabledChanged(bool value) => RaiseValidity();

    private void RaiseValidity()
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(BothUnlimited));
        OnPropertyChanged(nameof(BlockingMessage));
    }
}
