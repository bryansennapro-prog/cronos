using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using InstaPanel.App.Services;
using InstaPanel.Cloud;
using InstaPanel.Core;
using InstaPanel.Data;
using InstaPanel.Media;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InstaPanel.App.ViewModels;

public enum ComposeStage
{
    Editing,
    Preparing,
    Uploading,
    Publishing,
    Done,
    Failed,
}

public sealed partial class ComposeViewModel : ObservableObject
{
    public const double PreviewWidth = 300;
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMinutes(15);

    private readonly List<MediaItemViewModel> _selected = [];
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _coverCts;

    /// <summary>Post já criado no Worker. Depois disso, "tentar de novo" nunca cria outro post (evita duplicar).</summary>
    private string? _postId;

    /// <summary>Mídia escolhida na Biblioteca antes de abrir esta tela.</summary>
    public static string? PendingMediaId { get; set; }

    // ---------- Formato e mídias ----------

    [ObservableProperty] public partial PostKind Kind { get; set; } = PostKind.Image;

    public ObservableCollection<ComposeMediaViewModel> Items { get; } = [];

    public bool IsReel => Kind == PostKind.Reel;
    public bool IsStory => Kind == PostKind.Story;
    public bool IsCarousel => Kind == PostKind.Carousel;
    public bool CaptionEnabled => Kind.HasCaption();
    public bool HasMedia => _selected.Count > 0;
    public bool HasNoMedia => _selected.Count == 0;
    public bool CanAddMore => IsEditing && (IsCarousel ? _selected.Count < Kind.MediaCount().Max : _selected.Count == 0);

    public string AddButtonText => IsCarousel
        ? $"Adicionar itens ({_selected.Count}/{Kind.MediaCount().Max})"
        : "Escolher da biblioteca";

    public string KindHint => Kind switch
    {
        PostKind.Image => "Uma imagem no feed. Proporção entre 4:5 e 1.91:1 (o painel recorta se precisar).",
        PostKind.Carousel => "De 2 a 10 imagens e vídeos. O Instagram usa a proporção do primeiro item para todos.",
        PostKind.Reel => "Um vídeo vertical (9:16 recomendado), de 3 segundos a 15 minutos.",
        _ => "Uma imagem ou um vídeo de até 60 segundos, em 9:16. Fica no ar por 24 horas. Stories não levam legenda.",
    };

    private double? CarouselAspect =>
        IsCarousel && _selected.Count > 0 ? MediaRules.CarouselAspectFor(_selected[0].Item.Info) : null;

    /// <summary>Problemas que impedem publicar (quantidade, tipo, arquivos).</summary>
    private List<string> Problems()
    {
        var problems = new List<string>();
        var (min, max) = Kind.MediaCount();
        if (_selected.Count < min) problems.Add(IsCarousel ? $"Adicione pelo menos {min} itens (máximo {max})." : "Escolha uma mídia.");
        if (_selected.Count > max) problems.Add($"No máximo {max} itens.");
        foreach (var item in Items.Where(i => i.IsBlocked))
        {
            var reason = item.Media.FileExists ? string.Join(" ", item.Compat.Notes) : "arquivo não encontrado.";
            problems.Add(Items.Count > 1 ? $"Item {item.PositionText}: {reason}" : reason);
        }
        return problems;
    }

    public InfoBarSeverity CompatSeverity =>
        Problems().Count > 0 ? InfoBarSeverity.Error
        : Items.Any(i => i.Compat.Status == CompatibilityStatus.NeedsConversion) ? InfoBarSeverity.Informational
        : InfoBarSeverity.Success;

    public string CompatTitle => CompatSeverity switch
    {
        InfoBarSeverity.Error => "Ainda não dá para publicar",
        InfoBarSeverity.Informational => "Será convertido automaticamente",
        _ => "Pronto para publicar",
    };

    public string CompatMessage
    {
        get
        {
            var problems = Problems();
            if (problems.Count > 0) return string.Join(" ", problems);
            var notes = Items.Where(i => i.Compat.Notes.Count > 0)
                .Select(i => Items.Count > 1 ? $"Item {i.PositionText}: {string.Join(" ", i.Compat.Notes)}" : string.Join(" ", i.Compat.Notes));
            return string.Join(" ", notes);
        }
    }

    partial void OnKindChanged(PostKind value)
    {
        // Formatos de uma mídia só ficam com a primeira; mídias que o formato não aceita continuam
        // na lista (aparecem com erro) para a pessoa decidir o que trocar.
        if (value != PostKind.Carousel && _selected.Count > 1) _selected.RemoveRange(1, _selected.Count - 1);
        Refresh();
    }

    private void Refresh()
    {
        var aspect = CarouselAspect;
        Items.Clear();
        for (var i = 0; i < _selected.Count; i++)
        {
            var media = _selected[i];
            Items.Add(new ComposeMediaViewModel(media, i, _selected.Count, MediaRules.Evaluate(media.Item.Info, Kind, aspect)));
        }

        if (PreviewIndex >= _selected.Count) PreviewIndex = Math.Max(0, _selected.Count - 1);
        var reel = IsReel ? _selected.FirstOrDefault() : null;
        CoverMax = reel is { IsVideo: true } ? Math.Max(0.1, reel.Item.Info.Duration.TotalSeconds - 0.1) : 1;

        OnPropertyChanged(nameof(IsReel));
        OnPropertyChanged(nameof(IsStory));
        OnPropertyChanged(nameof(IsCarousel));
        OnPropertyChanged(nameof(CaptionEnabled));
        OnPropertyChanged(nameof(HasMedia));
        OnPropertyChanged(nameof(HasNoMedia));
        OnPropertyChanged(nameof(CanAddMore));
        OnPropertyChanged(nameof(AddButtonText));
        OnPropertyChanged(nameof(KindHint));
        OnPropertyChanged(nameof(CompatSeverity));
        OnPropertyChanged(nameof(CompatTitle));
        OnPropertyChanged(nameof(CompatMessage));
        OnPropertyChanged(nameof(CanPublish));
        OnPropertyChanged(nameof(PublishButtonText));
        RefreshPreview();
    }

    // ---------- Legenda ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CaptionCounterText), nameof(CaptionInvalid), nameof(CanPublish), nameof(PreviewCaption))]
    public partial string Caption { get; set; } = "";

    public CaptionStats Stats => CaptionStats.From(Caption);

    public string CaptionCounterText =>
        $"{Stats.Characters}/{CaptionStats.MaxCharacters} caracteres · {Stats.Hashtags}/{CaptionStats.MaxHashtags} hashtags · {Stats.Mentions}/{CaptionStats.MaxMentions} menções";

    public bool CaptionInvalid => !Stats.IsValid;

    public string PreviewCaption => string.IsNullOrWhiteSpace(Caption) ? "Sua legenda aparece aqui." : Caption.Trim();

    // ---------- Reel: feed e capa ----------

    [ObservableProperty] public partial bool ShareToFeed { get; set; } = true;

    [ObservableProperty] public partial double CoverMax { get; set; } = 1;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CoverText))]
    public partial double CoverSeconds { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(CoverText))]
    public partial bool CoverChosen { get; set; }

    [ObservableProperty] public partial ImageSource? CoverPreview { get; set; }

    public string CoverText => CoverChosen
        ? $"Capa: quadro em {Formatting.Duration(TimeSpan.FromSeconds(CoverSeconds))}"
        : "Capa padrão (a Meta usa o primeiro quadro). Arraste para escolher outro.";

    private bool _settingCover;

    partial void OnCoverSecondsChanged(double value)
    {
        if (_settingCover) return;
        _ = UpdateCoverAsync(value);
    }

    /// <summary>Gera a prévia do quadro escolhido (com atraso, para não rodar o FFmpeg a cada passo do slider).</summary>
    private async Task UpdateCoverAsync(double seconds)
    {
        _coverCts?.Cancel();
        var cts = _coverCts = new CancellationTokenSource();
        var reel = _selected.FirstOrDefault();
        var tools = AppServices.Ffmpeg;
        if (reel is not { IsVideo: true } || tools is null) return;
        try
        {
            await Task.Delay(250, cts.Token);
            var output = Path.Combine(AppServices.CacheDirectory, $"capa-{Guid.NewGuid():N}.jpg");
            await Task.Run(() => MediaConverter.ExtractFrameAsync(tools, reel.Item.Path, TimeSpan.FromSeconds(seconds), output, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;
            CoverPreview = new BitmapImage(new Uri(output));
            CoverChosen = true;
            RefreshPreview();
        }
        catch (OperationCanceledException)
        {
        }
        catch (MediaToolException)
        {
            // Sem prévia do quadro; a capa escolhida ainda é enviada.
            CoverChosen = true;
        }
    }

    [RelayCommand]
    private void ResetCover()
    {
        _coverCts?.Cancel();
        _settingCover = true;
        CoverSeconds = 0;
        _settingCover = false;
        CoverChosen = false;
        CoverPreview = null;
        RefreshPreview();
    }

    // ---------- Prévia ----------

    [ObservableProperty] public partial string Username { get; set; } = "sua_conta";
    [ObservableProperty] public partial ImageSource? ProfilePicture { get; set; }
    [ObservableProperty] public partial int PreviewIndex { get; set; }
    [ObservableProperty] public partial ImageSource? PreviewImage { get; set; }
    [ObservableProperty] public partial double PreviewHeight { get; set; } = PreviewWidth;
    [ObservableProperty] public partial string PreviewCounter { get; set; } = "";
    [ObservableProperty] public partial bool PreviewIsVideo { get; set; }

    partial void OnPreviewIndexChanged(int value) => RefreshPreview();

    private void RefreshPreview()
    {
        var current = _selected.Count == 0 ? null : _selected[Math.Clamp(PreviewIndex, 0, _selected.Count - 1)];
        PreviewImage = IsReel && CoverChosen && CoverPreview is not null ? CoverPreview : current?.Thumbnail;
        PreviewIsVideo = current?.IsVideo == true;
        PreviewCounter = IsCarousel && _selected.Count > 1 ? $"{PreviewIndex + 1}/{_selected.Count}" : "";
        PreviewHeight = Kind switch
        {
            PostKind.Reel or PostKind.Story => PreviewWidth * 16 / 9,
            PostKind.Carousel => PreviewWidth / (CarouselAspect ?? 1),
            _ => current is null ? PreviewWidth : PreviewWidth / Math.Clamp(current.Item.Info.AspectRatio, MediaRules.MinImageAspect, MediaRules.MaxImageAspect),
        };
    }

    [RelayCommand]
    private void PreviewPrevious() => PreviewIndex = Math.Max(0, PreviewIndex - 1);

    [RelayCommand]
    private void PreviewNext() => PreviewIndex = Math.Min(_selected.Count - 1, PreviewIndex + 1);

    // ---------- Seleção ----------

    public async Task LoadAsync()
    {
        if (PendingMediaId is { } id)
        {
            PendingMediaId = null;
            if (IsEditing) SelectFromLibrary(id);
        }

        if (ProfilePicture is not null) return;
        using var client = AppServices.TryCreateClient();
        if (client is null) return;
        try
        {
            var status = await client.GetAccountAsync();
            Username = status.Account.Username;
            if (Uri.TryCreate(status.Account.ProfilePictureUrl, UriKind.Absolute, out var pic)) ProfilePicture = new BitmapImage(pic);
        }
        catch (WorkerApiException)
        {
            // A prévia funciona sem a foto.
        }
    }

    /// <summary>"Criar post" na Biblioteca: começa um post novo com a mídia, no formato natural dela.</summary>
    public void SelectFromLibrary(string mediaId)
    {
        if (Stage is ComposeStage.Done or ComposeStage.Failed) ResetProgress();
        var media = Load(mediaId);
        if (media is null) return;
        _selected.Clear();
        _selected.Add(media);
        ResetCover();
        // Em Story/Carrossel mantém o formato; senão escolhe pelo tipo da mídia (imagem → post, vídeo → Reel).
        if (!IsCarousel && !IsStory) Kind = MediaRules.PostKindFor(media.Item.Info.Kind);
        Refresh();
    }

    /// <summary>Resultado do seletor: substitui (formatos de 1 mídia) ou acrescenta (carrossel).</summary>
    public void AddMedia(IReadOnlyList<string> mediaIds)
    {
        if (Stage is ComposeStage.Done or ComposeStage.Failed) ResetProgress();
        var loaded = mediaIds.Select(Load).OfType<MediaItemViewModel>().ToList();
        if (loaded.Count == 0) return;

        if (IsCarousel)
        {
            foreach (var m in loaded.Where(m => _selected.All(s => s.Id != m.Id)))
            {
                if (_selected.Count >= Kind.MediaCount().Max) break;
                _selected.Add(m);
            }
        }
        else
        {
            _selected.Clear();
            _selected.Add(loaded[0]);
            ResetCover();
        }
        Refresh();
    }

    public void Remove(ComposeMediaViewModel item)
    {
        _selected.RemoveAt(item.Index);
        if (item.Index == 0) ResetCover();
        Refresh();
    }

    public void Move(ComposeMediaViewModel item, int delta)
    {
        var target = item.Index + delta;
        if (target < 0 || target >= _selected.Count) return;
        (_selected[item.Index], _selected[target]) = (_selected[target], _selected[item.Index]);
        PreviewIndex = target;
        Refresh();
    }

    private static MediaItemViewModel? Load(string mediaId)
    {
        var item = AppServices.Library.GetMediaItem(mediaId);
        if (item is null) return null;
        return new MediaItemViewModel(item, AppServices.Library.GetPublishedMediaIds().Contains(item.Id));
    }

    public IReadOnlyList<MediaItemViewModel> GetLibraryItems()
    {
        var published = AppServices.Library.GetPublishedMediaIds();
        return AppServices.Library.GetMediaItems()
            .Select(i => new MediaItemViewModel(i, published.Contains(i.Id)))
            .Where(m => Kind.Accepts(m.Item.Info.Kind))
            .ToList();
    }

    // ---------- Publicação ----------

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy), nameof(IsEditing), nameof(ShowProgressPanel), nameof(CanPublish),
        nameof(CanCancel), nameof(IsDone), nameof(IsFailed), nameof(IsPublishingStage), nameof(CanAddMore),
        nameof(PrepareGlyph), nameof(UploadGlyph), nameof(PublishGlyph))]
    public partial ComposeStage Stage { get; set; } = ComposeStage.Editing;

    public bool IsBusy => Stage is ComposeStage.Preparing or ComposeStage.Uploading or ComposeStage.Publishing;
    public bool IsPublishingStage => Stage == ComposeStage.Publishing;
    /// <summary>Mídia e legenda só podem mudar enquanto o post não existe na nuvem.</summary>
    public bool IsEditing => Stage == ComposeStage.Editing || (Stage == ComposeStage.Failed && _postId is null);
    public bool ShowProgressPanel => Stage != ComposeStage.Editing;
    public bool IsDone => Stage == ComposeStage.Done;
    public bool IsFailed => Stage == ComposeStage.Failed;
    public bool CanCancel => Stage is ComposeStage.Preparing or ComposeStage.Uploading;

    /// <summary>Depois que o post existe na nuvem, só "Tentar de novo" (nunca um segundo post).</summary>
    public bool CanPublish => Problems().Count == 0 && (!CaptionEnabled || Stats.IsValid) && IsEditing;

    public string PublishButtonText => IsStory ? "Publicar story agora" : "Publicar agora";

    [ObservableProperty] public partial string StepText { get; set; } = "";
    [ObservableProperty] public partial double StepProgress { get; set; }
    [ObservableProperty] public partial bool StepIndeterminate { get; set; }
    [ObservableProperty] public partial string ErrorText { get; set; } = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPermalink))]
    public partial Uri? PermalinkUri { get; set; }

    public bool HasPermalink => PermalinkUri is not null;
    [ObservableProperty] public partial string DoneTitle { get; set; } = "";
    [ObservableProperty] public partial string DoneMessage { get; set; } = "";
    [ObservableProperty] public partial bool WasScheduled { get; set; }
    [ObservableProperty] public partial string PublishStepLabel { get; set; } = "Publicar no Instagram";

    private ComposeStage _reachedStage;

    // ✓ concluído, ● em andamento, ○ pendente (glyphs Segoe Fluent).
    public string PrepareGlyph => StepGlyph(ComposeStage.Preparing);
    public string UploadGlyph => StepGlyph(ComposeStage.Uploading);
    public string PublishGlyph => StepGlyph(ComposeStage.Publishing);

    private string StepGlyph(ComposeStage step)
    {
        var current = Stage is ComposeStage.Done or ComposeStage.Failed ? _reachedStage : Stage;
        if (Stage == ComposeStage.Done || current > step) return "";
        if (current == step) return Stage == ComposeStage.Failed ? "" : "";
        return "";
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private void NewPost()
    {
        _selected.Clear();
        Caption = "";
        ShareToFeed = true;
        ResetCover();
        ResetProgress();
        Refresh();
    }

    private void ResetProgress()
    {
        _postId = null;
        PermalinkUri = null;
        ErrorText = "";
        WasScheduled = false;
        Stage = ComposeStage.Editing;
        OnPropertyChanged(nameof(CanPublish));
    }

    public Task PublishNowAsync() => PublishAsync(null);

    /// <summary>
    /// Converte (se preciso) e envia cada mídia ao R2, depois cria o post no Worker. Sem horário:
    /// acompanha até publicar. Com horário: termina ao agendar — a nuvem cuida do resto.
    /// </summary>
    public async Task PublishAsync(DateTimeOffset? scheduledAt)
    {
        if (!CanPublish) return;
        ResetProgress();
        PublishStepLabel = scheduledAt is null ? "Publicar no Instagram" : "Agendar na nuvem";
        var media = _selected.ToList();
        var kind = Kind;
        var aspect = CarouselAspect;
        var tools = AppServices.Ffmpeg;
        using var client = AppServices.TryCreateClient();
        if (client is null)
        {
            Fail(ComposeStage.Preparing, "Configure o Worker em Configurações antes de publicar.");
            return;
        }
        if (tools is null)
        {
            Fail(ComposeStage.Preparing, "FFmpeg não encontrado. Instale com: winget install Gyan.FFmpeg");
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        var prepared = new List<PreparedMedia>();
        var n = media.Count;
        try
        {
            // 1. Preparar cada mídia
            Enter(ComposeStage.Preparing, "Preparando…");
            for (var i = 0; i < n; i++)
            {
                var m = media[i];
                var index = i;
                var label = n > 1 ? $"Preparando item {i + 1} de {n}" : m.IsVideo ? "Preparando o vídeo" : "Preparando a imagem";
                StepText = label + "…";
                var progress = new Progress<double>(p =>
                {
                    StepProgress = (index + p) / n * 100;
                    if (m.IsVideo) StepText = $"{label}… {p:P0}";
                });
                prepared.Add(await Task.Run(() => MediaConverter.PrepareAsync(
                    tools, m.Item.Path, m.Item.Info, AppServices.CacheDirectory, kind, aspect, progress, ct), ct));
            }

            // 2. Enviar cada mídia
            var sizes = prepared.Select(p => new FileInfo(p.Path).Length).ToList();
            var total = sizes.Sum();
            Enter(ComposeStage.Uploading, $"Enviando {Formatting.Size(total)} para a nuvem…");
            var keys = new List<string>();
            long sentBefore = 0;
            for (var i = 0; i < n; i++)
            {
                var offset = sentBefore;
                var progress = new Progress<UploadProgress>(p =>
                {
                    StepProgress = (double)(offset + p.BytesSent) / total * 100;
                    StepText = $"Enviando para a nuvem… {Formatting.Size(offset + p.BytesSent)} de {Formatting.Size(total)}";
                });
                var item = prepared[i];
                keys.Add(await Task.Run(() => client.UploadFileAsync(item.Path, item.ContentType, progress, ct), ct));
                sentBefore += sizes[i];
            }

            // 3. Criar o post — daqui em diante a publicação continua na nuvem mesmo se o app fechar.
            Enter(ComposeStage.Publishing, scheduledAt is null ? "Criando o post…" : "Agendando…");
            StepIndeterminate = true;
            var options = kind == PostKind.Reel
                ? new PostOptions(ShareToFeed, CoverChosen ? (int)Math.Round(CoverSeconds * 1000) : null)
                : null;
            var post = await client.CreatePostAsync(new CreatePostRequest(
                kind.ApiType(),
                kind.HasCaption() && !string.IsNullOrWhiteSpace(Caption) ? Caption.Trim() : null,
                keys.Select(k => new PostMediaKey(k)).ToList(),
                options,
                scheduledAt), CancellationToken.None);
            _postId = post.Id;
            SaveLocal(post, media.Select(m => m.Id).ToList());
            AppServices.NotifyPostsChanged();

            if (scheduledAt is not null)
            {
                _reachedStage = ComposeStage.Publishing;
                WasScheduled = true;
                DoneTitle = "Agendado!";
                DoneMessage = $"Será publicado {Formatting.Friendly(post.ScheduledAt)}. Pode fechar o painel e até desligar o PC.";
                StepIndeterminate = false;
                StepProgress = 100;
                Stage = ComposeStage.Done;
                return;
            }
            await FollowAsync(client, post, media.Select(m => m.Id).ToList());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            ResetProgress();
        }
        catch (Exception ex)
        {
            Fail(Stage, AppServices.Describe(ex));
        }
        finally
        {
            foreach (var p in prepared.Where(p => p.IsTemporary))
            {
                try { File.Delete(p.Path); } catch (IOException) { }
            }
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Tenta de novo: se o post ainda não existe, refaz tudo; se já existe, volta a acompanhar
    /// e, se ele falhou, pede ao Worker para refazer só a parte da Meta.
    /// </summary>
    [RelayCommand]
    private async Task RetryAsync()
    {
        if (_postId is null)
        {
            Stage = ComposeStage.Editing;
            await PublishNowAsync();
            return;
        }

        using var client = AppServices.TryCreateClient();
        if (client is null) return;
        try
        {
            Enter(ComposeStage.Publishing, "Tentando de novo…");
            StepIndeterminate = true;
            var post = await client.GetPostAsync(_postId);
            if (post.State == PostStates.Failed) post = await client.RetryPostAsync(_postId);
            await FollowAsync(client, post, null);
        }
        catch (Exception ex)
        {
            Fail(ComposeStage.Publishing, AppServices.Describe(ex));
        }
    }

    private async Task FollowAsync(WorkerClient client, PostInfo post, IReadOnlyList<string>? mediaIds)
    {
        var until = DateTime.UtcNow + PollTimeout;
        while (!PostStates.IsFinal(post.State))
        {
            StepText = $"{Formatting.PostState(post.State)}…" + (post.Error is { } e && post.Attempts > 0 ? $" (nova tentativa: {e})" : "");
            if (DateTime.UtcNow > until)
            {
                Fail(ComposeStage.Publishing, "A Meta está demorando. O post continua na fila da nuvem e será publicado assim que ficar pronto.");
                return;
            }
            await Task.Delay(PollInterval);
            try
            {
                post = await client.GetPostAsync(post.Id);
            }
            catch (WorkerApiException ex) when (ex.StatusCode == 0)
            {
                StepText = "Sem conexão — tentando acompanhar de novo…";
            }
        }

        SaveLocal(post, mediaIds);
        AppServices.NotifyPostsChanged();
        if (post.State == PostStates.Published)
        {
            _reachedStage = ComposeStage.Publishing;
            PermalinkUri = Uri.TryCreate(post.Permalink, UriKind.Absolute, out var link) ? link : null;
            DoneTitle = "Publicado!";
            DoneMessage = post.Type == "STORIES" ? "O story já está no ar (por 24 horas)." : "O post já está no seu perfil.";
            StepText = "Publicado no Instagram!";
            StepProgress = 100;
            StepIndeterminate = false;
            Stage = ComposeStage.Done;
        }
        else
        {
            Fail(ComposeStage.Publishing, post.Error ?? "A Meta recusou o post.");
        }
    }

    private void Enter(ComposeStage stage, string text)
    {
        _reachedStage = stage;
        Stage = stage;
        StepText = text;
        StepProgress = 0;
        StepIndeterminate = false;
        ErrorText = "";
    }

    private void Fail(ComposeStage at, string message)
    {
        _reachedStage = at is ComposeStage.Editing or ComposeStage.Done or ComposeStage.Failed ? _reachedStage : at;
        ErrorText = message;
        StepIndeterminate = false;
        Stage = ComposeStage.Failed;
    }

    /// <param name="mediaIds">Mídias da biblioteca na ordem do post; null mantém as já gravadas.</param>
    private static void SaveLocal(PostInfo post, IReadOnlyList<string>? mediaIds)
    {
        AppServices.Library.UpsertPost(new LocalPost
        {
            Id = post.Id,
            MediaItemId = mediaIds?.FirstOrDefault(),
            MediaItemIds = mediaIds,
            Type = post.Type,
            Caption = post.Caption,
            State = post.State,
            Permalink = post.Permalink,
            Error = post.Error,
            CreatedAt = post.CreatedAt,
            UpdatedAt = post.UpdatedAt,
        });
    }
}
