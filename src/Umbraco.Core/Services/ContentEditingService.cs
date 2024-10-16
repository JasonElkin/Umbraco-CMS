﻿using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Umbraco.Cms.Core.DependencyInjection;
using Umbraco.Cms.Core.Models;
using Umbraco.Cms.Core.Models.ContentEditing;
using Umbraco.Cms.Core.Models.Membership;
using Umbraco.Cms.Core.PropertyEditors;
using Umbraco.Cms.Core.Scoping;
using Umbraco.Cms.Core.Services.OperationStatus;
using Umbraco.Extensions;

namespace Umbraco.Cms.Core.Services;

internal sealed class ContentEditingService
    : ContentEditingServiceWithSortingBase<IContent, IContentType, IContentService, IContentTypeService>, IContentEditingService
{
    private readonly ITemplateService _templateService;
    private readonly ILogger<ContentEditingService> _logger;
    private readonly IUserService _userService;
    private readonly ILocalizationService _localizationService;
    private readonly ILanguageService _languageService;

    [Obsolete("Use non-obsolete constructor. This will be removed in Umbraco 16.")]
    public ContentEditingService(
        IContentService contentService,
        IContentTypeService contentTypeService,
        PropertyEditorCollection propertyEditorCollection,
        IDataTypeService dataTypeService,
        ITemplateService templateService,
        ILogger<ContentEditingService> logger,
        ICoreScopeProvider scopeProvider,
        IUserIdKeyResolver userIdKeyResolver,
        ITreeEntitySortingService treeEntitySortingService,
        IContentValidationService contentValidationService)
        : this(
            contentService,
            contentTypeService,
            propertyEditorCollection,
            dataTypeService,
            templateService,
            logger,
            scopeProvider,
            userIdKeyResolver,
            treeEntitySortingService,
            contentValidationService,
            StaticServiceProvider.Instance.GetRequiredService<IUserService>(),
            StaticServiceProvider.Instance.GetRequiredService<ILocalizationService>(),
            StaticServiceProvider.Instance.GetRequiredService<ILanguageService>()
            )
    {

    }

    public ContentEditingService(
        IContentService contentService,
        IContentTypeService contentTypeService,
        PropertyEditorCollection propertyEditorCollection,
        IDataTypeService dataTypeService,
        ITemplateService templateService,
        ILogger<ContentEditingService> logger,
        ICoreScopeProvider scopeProvider,
        IUserIdKeyResolver userIdKeyResolver,
        ITreeEntitySortingService treeEntitySortingService,
        IContentValidationService contentValidationService,
        IUserService userService,
        ILocalizationService localizationService,
        ILanguageService languageService)
        : base(contentService, contentTypeService, propertyEditorCollection, dataTypeService, logger, scopeProvider, userIdKeyResolver, contentValidationService, treeEntitySortingService)
    {
        _templateService = templateService;
        _logger = logger;
        _userService = userService;
        _localizationService = localizationService;
        _languageService = languageService;
    }

    public async Task<IContent?> GetAsync(Guid key)
    {
        IContent? content = ContentService.GetById(key);
        return await Task.FromResult(content);
    }

    [Obsolete("Please use the validate update method that is not obsoleted. Will be removed in V16.")]
    public async Task<Attempt<ContentValidationResult, ContentEditingOperationStatus>> ValidateUpdateAsync(Guid key, ContentUpdateModel updateModel)
    {
        IContent? content = ContentService.GetById(key);
        return content is not null
            ? await ValidateCulturesAndPropertiesAsync(updateModel, content.ContentType.Key)
            : Attempt.FailWithStatus(ContentEditingOperationStatus.NotFound, new ContentValidationResult());
    }

    public async Task<Attempt<ContentValidationResult, ContentEditingOperationStatus>> ValidateUpdateAsync(Guid key, ValidateContentUpdateModel updateModel)
    {
        IContent? content = ContentService.GetById(key);
        return content is not null
            ? await ValidateCulturesAndPropertiesAsync(updateModel, content.ContentType.Key, updateModel.Cultures)
            : Attempt.FailWithStatus(ContentEditingOperationStatus.NotFound, new ContentValidationResult());
    }

    public async Task<Attempt<ContentValidationResult, ContentEditingOperationStatus>> ValidateCreateAsync(ContentCreateModel createModel)
        => await ValidateCulturesAndPropertiesAsync(createModel, createModel.ContentTypeKey, createModel.Variants.Select(variant => variant.Culture));

    public async Task<Attempt<ContentCreateResult, ContentEditingOperationStatus>> CreateAsync(ContentCreateModel createModel, Guid userKey)
    {
        if (await ValidateCulturesAsync(createModel) is false)
        {
            return Attempt.FailWithStatus(ContentEditingOperationStatus.InvalidCulture, new ContentCreateResult());
        }

        Attempt<ContentCreateResult, ContentEditingOperationStatus> result = await MapCreate<ContentCreateResult>(createModel);
        if (result.Success == false)
        {
            return result;
        }

        // the create mapping might succeed, but this doesn't mean the model is valid at property level.
        // we'll return the actual property validation status if the entire operation succeeds.
        ContentEditingOperationStatus validationStatus = result.Status;
        ContentValidationResult validationResult = result.Result.ValidationResult;

        IContent content = await EnsureOnlyAllowedFieldsAreUpdated(result.Result.Content!, userKey);
        ContentEditingOperationStatus updateTemplateStatus = await UpdateTemplateAsync(content, createModel.TemplateKey);
        if (updateTemplateStatus != ContentEditingOperationStatus.Success)
        {
            return Attempt.FailWithStatus(updateTemplateStatus, new ContentCreateResult { Content = content });
        }

        ContentEditingOperationStatus saveStatus = await Save(content, userKey);
        return saveStatus == ContentEditingOperationStatus.Success
            ? Attempt.SucceedWithStatus(validationStatus, new ContentCreateResult { Content = content, ValidationResult = validationResult })
            : Attempt.FailWithStatus(saveStatus, new ContentCreateResult { Content = content });
    }

    /// <summary>
    /// A temporary method that ensures the data is sent in is overridden by the original data, in cases where the user do not have permissions to change the data.
    /// </summary>
    private async Task<IContent> EnsureOnlyAllowedFieldsAreUpdated(IContent contentWithPotentialUnallowedChanges, Guid userKey)
    {
        if (contentWithPotentialUnallowedChanges.ContentType.VariesByCulture() is false)
        {
            return contentWithPotentialUnallowedChanges;
        }

        IContent? existingContent = await GetAsync(contentWithPotentialUnallowedChanges.Key);

        IUser? user = await _userService.GetAsync(userKey);

        if (user is null)
        {
            return contentWithPotentialUnallowedChanges;
        }

        var allowedLanguageIds = user.CalculateAllowedLanguageIds(_localizationService)!;

        var allowedCultures = (await _languageService.GetIsoCodesByIdsAsync(allowedLanguageIds)).ToHashSet();

        foreach (var culture in contentWithPotentialUnallowedChanges.EditedCultures ?? contentWithPotentialUnallowedChanges.PublishedCultures)
        {
            if (allowedCultures.Contains(culture))
            {
                continue;
            }


            // else override the updates values with the original values.
            foreach (IProperty property in contentWithPotentialUnallowedChanges.Properties)
            {
                if (property.PropertyType.VariesByCulture() is false)
                {
                    continue;
                }

                var value = existingContent?.Properties.First(x=>x.Alias == property.Alias).GetValue(culture, null, false);
                property.SetValue(value, culture, null);
            }
        }

       return contentWithPotentialUnallowedChanges;
    }

    public async Task<Attempt<ContentUpdateResult, ContentEditingOperationStatus>> UpdateAsync(Guid key, ContentUpdateModel updateModel, Guid userKey)
    {
        IContent? content = ContentService.GetById(key);
        if (content is null)
        {
            return Attempt.FailWithStatus(ContentEditingOperationStatus.NotFound, new ContentUpdateResult());
        }

        if (await ValidateCulturesAsync(updateModel) is false)
        {
            return Attempt.FailWithStatus(ContentEditingOperationStatus.InvalidCulture, new ContentUpdateResult { Content = content });
        }

        Attempt<ContentUpdateResult, ContentEditingOperationStatus> result = await MapUpdate<ContentUpdateResult>(content, updateModel);
        if (result.Success == false)
        {
            return Attempt.FailWithStatus(result.Status, result.Result);
        }

        // the update mapping might succeed, but this doesn't mean the model is valid at property level.
        // we'll return the actual property validation status if the entire operation succeeds.
        ContentEditingOperationStatus validationStatus = result.Status;
        ContentValidationResult validationResult = result.Result.ValidationResult;

        content = await EnsureOnlyAllowedFieldsAreUpdated(content, userKey);

        ContentEditingOperationStatus updateTemplateStatus = await UpdateTemplateAsync(content, updateModel.TemplateKey);
        if (updateTemplateStatus != ContentEditingOperationStatus.Success)
        {
            return Attempt.FailWithStatus(updateTemplateStatus, new ContentUpdateResult { Content = content });
        }

        ContentEditingOperationStatus saveStatus = await Save(content, userKey);
        return saveStatus == ContentEditingOperationStatus.Success
            ? Attempt.SucceedWithStatus(validationStatus, new ContentUpdateResult { Content = content, ValidationResult = validationResult })
            : Attempt.FailWithStatus(saveStatus, new ContentUpdateResult { Content = content });
    }

    public async Task<Attempt<IContent?, ContentEditingOperationStatus>> MoveToRecycleBinAsync(Guid key, Guid userKey)
        => await HandleMoveToRecycleBinAsync(key, userKey);

    public async Task<Attempt<IContent?, ContentEditingOperationStatus>> DeleteFromRecycleBinAsync(Guid key, Guid userKey)
        => await HandleDeleteAsync(key, userKey, true);

    public async Task<Attempt<IContent?, ContentEditingOperationStatus>> DeleteAsync(Guid key, Guid userKey)
        => await HandleDeleteAsync(key, userKey, false);

    public async Task<Attempt<IContent?, ContentEditingOperationStatus>> MoveAsync(Guid key, Guid? parentKey, Guid userKey)
        => await HandleMoveAsync(key, parentKey, userKey);

    public async Task<Attempt<IContent?, ContentEditingOperationStatus>> RestoreAsync(Guid key, Guid? parentKey, Guid userKey)
        => await HandleMoveAsync(key, parentKey, userKey, true);

    public async Task<Attempt<IContent?, ContentEditingOperationStatus>> CopyAsync(Guid key, Guid? parentKey, bool relateToOriginal, bool includeDescendants, Guid userKey)
        => await HandleCopyAsync(key, parentKey, relateToOriginal, includeDescendants, userKey);

    public async Task<ContentEditingOperationStatus> SortAsync(Guid? parentKey, IEnumerable<SortingModel> sortingModels, Guid userKey)
        => await HandleSortAsync(parentKey, sortingModels, userKey);

    private async Task<Attempt<ContentValidationResult, ContentEditingOperationStatus>> ValidateCulturesAndPropertiesAsync(
        ContentEditingModelBase contentEditingModelBase,
        Guid contentTypeKey,
        IEnumerable<string?>? culturesToValidate = null)
        => await ValidateCulturesAsync(contentEditingModelBase) is false
            ? Attempt.FailWithStatus(ContentEditingOperationStatus.InvalidCulture, new ContentValidationResult())
            : await ValidatePropertiesAsync(contentEditingModelBase, contentTypeKey, culturesToValidate);

    private async Task<ContentEditingOperationStatus> UpdateTemplateAsync(IContent content, Guid? templateKey)
    {
        if (templateKey == null)
        {
            content.TemplateId = null;
            return ContentEditingOperationStatus.Success;
        }

        ITemplate? template = await _templateService.GetAsync(templateKey.Value);
        if (template == null)
        {
            return ContentEditingOperationStatus.TemplateNotFound;
        }

        IContentType contentType = ContentTypeService.Get(content.ContentTypeId)
                                   ?? throw new ArgumentException("The content type was not found", nameof(content));
        if (contentType.IsAllowedTemplate(template.Alias) == false)
        {
            return ContentEditingOperationStatus.TemplateNotAllowed;
        }

        content.TemplateId = template.Id;
        return ContentEditingOperationStatus.Success;
    }

    protected override IContent New(string? name, int parentId, IContentType contentType)
        => new Content(name, parentId, contentType);

    protected override OperationResult? Move(IContent content, int newParentId, int userId)
        => ContentService.Move(content, newParentId, userId);

    protected override IContent? Copy(IContent content, int newParentId, bool relateToOriginal, bool includeDescendants, int userId)
        => ContentService.Copy(content, newParentId, relateToOriginal, includeDescendants, userId);

    protected override OperationResult? MoveToRecycleBin(IContent content, int userId)
        => ContentService.MoveToRecycleBin(content, userId);

    protected override OperationResult? Delete(IContent content, int userId)
        => ContentService.Delete(content, userId);

    protected override IEnumerable<IContent> GetPagedChildren(int parentId, int pageIndex, int pageSize, out long total)
        => ContentService.GetPagedChildren(parentId, pageIndex, pageSize, out total);

    protected override ContentEditingOperationStatus Sort(IEnumerable<IContent> items, int userId)
    {
        OperationResult result = ContentService.Sort(items, userId);
        return OperationResultToOperationStatus(result);
    }

    private async Task<ContentEditingOperationStatus> Save(IContent content, Guid userKey)
    {
        try
        {
            var currentUserId = await GetUserIdAsync(userKey);
            OperationResult saveResult = ContentService.Save(content, currentUserId);
            return saveResult.Result switch
            {
                // these are the only result states currently expected from Save
                OperationResultType.Success => ContentEditingOperationStatus.Success,
                OperationResultType.FailedCancelledByEvent => ContentEditingOperationStatus.CancelledByNotification,

                // for any other state we'll return "unknown" so we know that we need to amend this
                _ => ContentEditingOperationStatus.Unknown
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Content save operation failed");
            return ContentEditingOperationStatus.Unknown;
        }
    }
}
