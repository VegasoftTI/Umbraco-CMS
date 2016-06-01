﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using NPoco;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;
using Umbraco.Core.Models.EntityBase;
using Umbraco.Core.Models.Membership;
using Umbraco.Core.Models.Rdbms;

using Umbraco.Core.Persistence.DatabaseModelDefinitions;
using Umbraco.Core.Persistence.Factories;
using Umbraco.Core.Persistence.Querying;
using Umbraco.Core.Cache;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.Persistence.Mappers;
using Umbraco.Core.Persistence.SqlSyntax;
using Umbraco.Core.Persistence.UnitOfWork;

namespace Umbraco.Core.Persistence.Repositories
{
    /// <summary>
    /// Represents a repository for doing CRUD operations for <see cref="IContent"/>.
    /// </summary>
    internal class ContentRepository : RecycleBinRepository<int, IContent, ContentRepository>, IContentRepository
    {
        private readonly IContentTypeRepository _contentTypeRepository;
        private readonly ITemplateRepository _templateRepository;
        private readonly ITagRepository _tagRepository;
        private readonly CacheHelper _cacheHelper;

        public ContentRepository(IDatabaseUnitOfWork work, CacheHelper cacheHelper, ILogger logger, IContentTypeRepository contentTypeRepository, ITemplateRepository templateRepository, ITagRepository tagRepository, IContentSection contentSection, IMappingResolver mappingResolver)
            : base(work, cacheHelper, logger, contentSection, mappingResolver)
        {
            if (contentTypeRepository == null) throw new ArgumentNullException(nameof(contentTypeRepository));
            if (templateRepository == null) throw new ArgumentNullException(nameof(templateRepository));
            if (tagRepository == null) throw new ArgumentNullException(nameof(tagRepository));
            _contentTypeRepository = contentTypeRepository;
            _templateRepository = templateRepository;
            _tagRepository = tagRepository;
            _cacheHelper = cacheHelper;

            EnsureUniqueNaming = true;
        }

        public void SetNoCachePolicy()
        {
            // using NoCache here means that we are NOT updating the cache
            // so this should be OK for reads but NOT for writes!
            CachePolicy = new NoCacheRepositoryCachePolicy<IContent, int>();
        }

        protected override ContentRepository Instance => this;

        public bool EnsureUniqueNaming { get; set; }

        #region Overrides of RepositoryBase<IContent>

        protected override IContent PerformGet(int id)
        {
            var sql = GetBaseQuery(false)
                .Where(GetBaseWhereClause(), new { Id = id })
                .Where<DocumentDto>(x => x.Newest)
                .OrderByDescending<ContentVersionDto>(x => x.VersionDate);

            var dto = Database.Fetch<DocumentDto>(sql).FirstOrDefault();

            if (dto == null)
                return null;

            var content = CreateContentFromDto(dto, dto.ContentVersionDto.VersionId);

            return content;
        }

        protected override IEnumerable<IContent> PerformGetAll(params int[] ids)
        {
            var sql = GetBaseQuery(false);
            if (ids.Any())
            {
                sql.Where("umbracoNode.id in (@ids)", new { /*ids =*/ ids });
            }

            //we only want the newest ones with this method
            sql.Where<DocumentDto>(x => x.Newest);

            return MapQueryDtos(Database.Fetch<DocumentDto>(sql));
        }

        protected override IEnumerable<IContent> PerformGetByQuery(IQuery<IContent> query)
        {
            var sqlClause = GetBaseQuery(false);
            var translator = new SqlTranslator<IContent>(sqlClause, query);
            var sql = translator.Translate()
                                .Where<DocumentDto>(x => x.Newest)
                                //.OrderByDescending<ContentVersionDto>(x => x.VersionDate)
                                .OrderBy<NodeDto>(x => x.Level)
                                .OrderBy<NodeDto>(x => x.SortOrder);

            return MapQueryDtos(Database.Fetch<DocumentDto>(sql));
        }

        #endregion

        #region Overrides of NPocoRepositoryBase<IContent>

        protected override Sql<SqlContext> GetBaseQuery(bool isCount)
        {
            var sqlx = string.Format("LEFT OUTER JOIN {0} {1} ON ({1}.{2}={0}.{2} AND {1}.{3}=1)",
                SqlSyntax.GetQuotedTableName("cmsDocument"),
                SqlSyntax.GetQuotedTableName("cmsDocument2"),
                SqlSyntax.GetQuotedColumnName("nodeId"),
                SqlSyntax.GetQuotedColumnName("published"));

            var sql = Sql();

            sql = isCount
                ? sql.SelectCount()
                : sql.Select<DocumentDto>(r =>
                        r.Select<ContentVersionDto>(rr =>
                            rr.Select<ContentDto>(rrr =>
                                rrr.Select<NodeDto>()))
                         .Select<DocumentPublishedReadOnlyDto>(tableAlias: "cmsDocument2"));

            sql
                .From<DocumentDto>()
                .InnerJoin<ContentVersionDto>()
                .On<DocumentDto, ContentVersionDto>(left => left.VersionId, right => right.VersionId)
                .InnerJoin<ContentDto>()
                .On<ContentVersionDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                .InnerJoin<NodeDto>()
                .On<ContentDto, NodeDto>(left => left.NodeId, right => right.NodeId)

                // cannot do this because NPoco does not know how to alias the table
                //.LeftOuterJoin<DocumentPublishedReadOnlyDto>()
                //.On<DocumentDto, DocumentPublishedReadOnlyDto>(left => left.NodeId, right => right.NodeId)
                // so have to rely on writing our own SQL
                .Append(sqlx/*, new { @published = true }*/)

                .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId);
            return sql;
        }

        protected override string GetBaseWhereClause()
        {
            return "umbracoNode.id = @Id";
        }

        protected override IEnumerable<string> GetDeleteClauses()
        {
            var list = new List<string>
                           {
                               "DELETE FROM umbracoRedirectUrl WHERE contentKey IN (SELECT uniqueID FROM umbracoNode WHERE id = @Id)",
                               "DELETE FROM cmsTask WHERE nodeId = @Id",
                               "DELETE FROM umbracoUser2NodeNotify WHERE nodeId = @Id",
                               "DELETE FROM umbracoUser2NodePermission WHERE nodeId = @Id",
                               "DELETE FROM umbracoRelation WHERE parentId = @Id",
                               "DELETE FROM umbracoRelation WHERE childId = @Id",
                               "DELETE FROM cmsTagRelationship WHERE nodeId = @Id",
                               "DELETE FROM umbracoDomains WHERE domainRootStructureID = @Id",
                               "DELETE FROM cmsDocument WHERE nodeId = @Id",
                               "DELETE FROM cmsPropertyData WHERE contentNodeId = @Id",
                               "DELETE FROM cmsPreviewXml WHERE nodeId = @Id",
                               "DELETE FROM cmsContentVersion WHERE ContentId = @Id",
                               "DELETE FROM cmsContentXml WHERE nodeId = @Id",
                               "DELETE FROM cmsContent WHERE nodeId = @Id",
                               "DELETE FROM umbracoAccess WHERE nodeId = @Id",
                               "DELETE FROM umbracoNode WHERE id = @Id"
                           };
            return list;
        }

        protected override Guid NodeObjectTypeId => new Guid(Constants.ObjectTypes.Document);

        #endregion

        #region Overrides of VersionableRepositoryBase<IContent>

        public override IContent GetByVersion(Guid versionId)
        {
            var sql = GetBaseQuery(false);
            sql.Where("cmsContentVersion.VersionId = @VersionId", new { VersionId = versionId });
            sql.OrderByDescending<ContentVersionDto>(x => x.VersionDate);

            var dto = Database.Fetch<DocumentDto>(sql).FirstOrDefault();

            if (dto == null)
                return null;

            var content = CreateContentFromDto(dto, versionId);

            return content;
        }

        public override void DeleteVersion(Guid versionId)
        {
            var sql = Sql()
                .SelectAll()
                .From<DocumentDto>()
                .InnerJoin<ContentVersionDto>().On<ContentVersionDto, DocumentDto>(left => left.VersionId, right => right.VersionId)
                .Where<ContentVersionDto>(x => x.VersionId == versionId)
                .Where<DocumentDto>(x => x.Newest != true);
            var dto = Database.Fetch<DocumentDto>(sql).FirstOrDefault();

            if (dto == null) return;

            PerformDeleteVersion(dto.NodeId, versionId);
        }

        public override void DeleteVersions(int id, DateTime versionDate)
        {
            var sql = Sql()
                .SelectAll()
                .From<DocumentDto>()
                .InnerJoin<ContentVersionDto>().On<ContentVersionDto, DocumentDto>(left => left.VersionId, right => right.VersionId)
                .Where<ContentVersionDto>(x => x.NodeId == id)
                .Where<ContentVersionDto>(x => x.VersionDate < versionDate)
                .Where<DocumentDto>(x => x.Newest != true);
            var list = Database.Fetch<DocumentDto>(sql);
            if (list.Any() == false) return;

            foreach (var dto in list)
            {
                PerformDeleteVersion(id, dto.VersionId);
            }
        }

        protected override void PerformDeleteVersion(int id, Guid versionId)
        {
            // raise event first else potential FK issues
            OnUowRemovingVersion(new UnitOfWorkVersionEventArgs(UnitOfWork, id, versionId));

            Database.Delete<PropertyDataDto>("WHERE contentNodeId = @Id AND versionId = @VersionId", new { Id = id, VersionId = versionId });
            Database.Delete<ContentVersionDto>("WHERE ContentId = @Id AND VersionId = @VersionId", new { Id = id, VersionId = versionId });
            Database.Delete<DocumentDto>("WHERE nodeId = @Id AND versionId = @VersionId", new { Id = id, VersionId = versionId });
        }

        #endregion

        #region Unit of Work Implementation

        protected override void PersistDeletedItem(IContent entity)
        {
            // raise event first else potential FK issues
            OnUowRemovingEntity(new UnitOfWorkEntityEventArgs(UnitOfWork, entity));

            //We need to clear out all access rules but we need to do this in a manual way since
            // nothing in that table is joined to a content id
            var subQuery = Sql()
                .Select("umbracoAccessRule.accessId")
                .From<AccessRuleDto>()
                .InnerJoin<AccessDto>()
                .On<AccessRuleDto, AccessDto>(left => left.AccessId, right => right.Id)
                .Where<AccessDto>(dto => dto.NodeId == entity.Id);
            Database.Execute(SqlSyntax.GetDeleteSubquery("umbracoAccessRule", "accessId", subQuery));

            //now let the normal delete clauses take care of everything else
            base.PersistDeletedItem(entity);
        }

        protected override void PersistNewItem(IContent entity)
        {
            ((Content)entity).AddingEntity();

            //ensure the default template is assigned
            if (entity.Template == null)
                entity.Template = entity.ContentType.DefaultTemplate;

            //Ensure unique name on the same level
            entity.Name = EnsureUniqueNodeName(entity.ParentId, entity.Name);

            //Ensure that strings don't contain characters that are invalid in XML
            entity.SanitizeEntityPropertiesForXmlStorage();

            var factory = new ContentFactory(NodeObjectTypeId, entity.Id);
            var dto = factory.BuildDto(entity);

            //NOTE Should the logic below have some kind of fallback for empty parent ids ?
            //Logic for setting Path, Level and SortOrder
            var parent = Database.First<NodeDto>("WHERE id = @ParentId", new { /*ParentId =*/ entity.ParentId });
            var level = parent.Level + 1;
            var maxSortOrder = Database.ExecuteScalar<int>(
                "SELECT coalesce(max(sortOrder),-1) FROM umbracoNode WHERE parentId = @ParentId AND nodeObjectType = @NodeObjectType",
                new { /*ParentId =*/ entity.ParentId, NodeObjectType = NodeObjectTypeId });
            var sortOrder = maxSortOrder + 1;

            //Create the (base) node data - umbracoNode
            var nodeDto = dto.ContentVersionDto.ContentDto.NodeDto;
            nodeDto.Path = parent.Path;
            nodeDto.Level = short.Parse(level.ToString(CultureInfo.InvariantCulture));
            nodeDto.SortOrder = sortOrder;
            var o = Database.IsNew(nodeDto) ? Convert.ToInt32(Database.Insert(nodeDto)) : Database.Update(nodeDto);

            //Update with new correct path
            nodeDto.Path = string.Concat(parent.Path, ",", nodeDto.NodeId);
            Database.Update(nodeDto);

            //Update entity with correct values
            entity.Id = nodeDto.NodeId; //Set Id on entity to ensure an Id is set
            entity.Path = nodeDto.Path;
            entity.SortOrder = sortOrder;
            entity.Level = level;

            //Assign the same permissions to it as the parent node
            // http://issues.umbraco.org/issue/U4-2161
            // fixme STOP new-ing repos everywhere!
            // var prepo = UnitOfWork.CreateRepository<IPermissionRepository<IContent>>();
            var permissionsRepo = new PermissionRepository<IContent>(UnitOfWork, _cacheHelper);
            var parentPermissions = permissionsRepo.GetPermissionsForEntity(entity.ParentId).ToArray();
            //if there are parent permissions then assign them, otherwise leave null and permissions will become the
            // user's default permissions.
            if (parentPermissions.Any())
            {
                var userPermissions = (
                    from perm in parentPermissions
                    from p in perm.AssignedPermissions
                    select new EntityPermissionSet.UserPermission(perm.UserId, p)).ToList();

                permissionsRepo.ReplaceEntityPermissions(new EntityPermissionSet(entity.Id, userPermissions));
                //flag the entity's permissions changed flag so we can track those changes.
                //Currently only used for the cache refreshers to detect if we should refresh all user permissions cache.
                ((Content)entity).PermissionsChanged = true;
            }

            //Create the Content specific data - cmsContent
            var contentDto = dto.ContentVersionDto.ContentDto;
            contentDto.NodeId = nodeDto.NodeId;
            Database.Insert(contentDto);

            //Create the first version - cmsContentVersion
            //Assumes a new Version guid and Version date (modified date) has been set
            var contentVersionDto = dto.ContentVersionDto;
            contentVersionDto.NodeId = nodeDto.NodeId;
            Database.Insert(contentVersionDto);

            //Create the Document specific data for this version - cmsDocument
            //Assumes a new Version guid has been generated
            dto.NodeId = nodeDto.NodeId;
            Database.Insert(dto);

            //Create the PropertyData for this version - cmsPropertyData
            var propertyFactory = new PropertyFactory(entity.ContentType.CompositionPropertyTypes.ToArray(), entity.Version, entity.Id);
            var propertyDataDtos = propertyFactory.BuildDto(entity.Properties);
            var keyDictionary = new Dictionary<int, int>();

            //Add Properties
            foreach (var propertyDataDto in propertyDataDtos)
            {
                var primaryKey = Convert.ToInt32(Database.Insert(propertyDataDto));
                keyDictionary.Add(propertyDataDto.PropertyTypeId, primaryKey);
            }

            //Update Properties with its newly set Id
            foreach (var property in entity.Properties)
                property.Id = keyDictionary[property.PropertyTypeId];

            //lastly, check if we are a creating a published version , then update the tags table
            if (entity.Published)
                UpdateEntityTags(entity, _tagRepository);

            // published => update published version infos, else leave it blank
            if (entity.Published)
            {
                dto.DocumentPublishedReadOnlyDto = new DocumentPublishedReadOnlyDto
                {
                    VersionId = dto.VersionId,
                    Newest = true,
                    NodeId = dto.NodeId,
                    Published = true
                };
                ((Content)entity).PublishedVersionGuid = dto.VersionId;
            }

            OnUowRefreshedEntity(new UnitOfWorkEntityEventArgs(UnitOfWork, entity));

            entity.ResetDirtyProperties();
        }

        protected override void PersistUpdatedItem(IContent entity)
        {
            var content = (Content) entity;
            var publishedState = content.PublishedState;
            var publishedStateChanged = publishedState == PublishedState.Publishing || publishedState == PublishedState.Unpublishing;

            //check if we need to make any database changes at all
            if (entity.RequiresSaving(publishedState) == false)
            {
                entity.ResetDirtyProperties();
                return;
            }

            //check if we need to create a new version
            var requiresNewVersion = entity.RequiresNewVersion(publishedState);
            if (requiresNewVersion)
            {
                //Updates Modified date and Version Guid
                content.UpdatingEntity();
            }
            else
            {
                entity.UpdateDate = DateTime.Now;
            }

            //Ensure unique name on the same level
            entity.Name = EnsureUniqueNodeName(entity.ParentId, entity.Name, entity.Id);

            //Ensure that strings don't contain characters that are invalid in XML
            entity.SanitizeEntityPropertiesForXmlStorage();

            //Look up parent to get and set the correct Path and update SortOrder if ParentId has changed
            if (entity.IsPropertyDirty("ParentId"))
            {
                var parent = Database.First<NodeDto>("WHERE id = @ParentId", new { /*ParentId =*/ entity.ParentId });
                entity.Path = string.Concat(parent.Path, ",", entity.Id);
                entity.Level = parent.Level + 1;
                entity.SortOrder = NextChildSortOrder(entity.ParentId);

                //Question: If we move a node, should we update permissions to inherit from the new parent if the parent has permissions assigned?
                // if we do that, then we'd need to propogate permissions all the way downward which might not be ideal for many people.
                // Gonna just leave it as is for now, and not re-propogate permissions.
            }

            var factory = new ContentFactory(NodeObjectTypeId, entity.Id);
            //Look up Content entry to get Primary for updating the DTO
            var contentDto = Database.SingleOrDefault<ContentDto>("WHERE nodeId = @Id", new { /*Id =*/ entity.Id });
            factory.SetPrimaryKey(contentDto.PrimaryKey);
            var dto = factory.BuildDto(entity);

            //Updates the (base) node data - umbracoNode
            var nodeDto = dto.ContentVersionDto.ContentDto.NodeDto;
            var o = Database.Update(nodeDto);

            //Only update this DTO if the contentType has actually changed
            if (contentDto.ContentTypeId != entity.ContentTypeId)
            {
                //Create the Content specific data - cmsContent
                var newContentDto = dto.ContentVersionDto.ContentDto;
                Database.Update(newContentDto);
            }

            //If Published state has changed then previous versions should have their publish state reset.
            //If state has been changed to unpublished the previous versions publish state should also be reset.
            //if (((ICanBeDirty)entity).IsPropertyDirty("Published") && (entity.Published || publishedState == PublishedState.Unpublished))
            if (entity.RequiresClearPublishedFlag(publishedState, requiresNewVersion))
                ClearPublishedFlag(entity);

            //Look up (newest) entries by id in cmsDocument table to set newest = false
            ClearNewestFlag(entity);

            var contentVersionDto = dto.ContentVersionDto;
            if (requiresNewVersion)
            {
                //Create a new version - cmsContentVersion
                //Assumes a new Version guid and Version date (modified date) has been set
                Database.Insert(contentVersionDto);
                //Create the Document specific data for this version - cmsDocument
                //Assumes a new Version guid has been generated
                Database.Insert(dto);
            }
            else
            {
                //In order to update the ContentVersion we need to retrieve its primary key id
                var contentVerDto = Database.SingleOrDefault<ContentVersionDto>("WHERE VersionId = @Version", new { /*Version =*/ entity.Version });
                contentVersionDto.Id = contentVerDto.Id;

                Database.Update(contentVersionDto);
                Database.Update(dto);
            }

            //Create the PropertyData for this version - cmsPropertyData
            var propertyFactory = new PropertyFactory(entity.ContentType.CompositionPropertyTypes.ToArray(), entity.Version, entity.Id);
            var propertyDataDtos = propertyFactory.BuildDto(entity.Properties);
            var keyDictionary = new Dictionary<int, int>();

            //Add Properties
            foreach (var propertyDataDto in propertyDataDtos)
            {
                if (requiresNewVersion == false && propertyDataDto.Id > 0)
                {
                    Database.Update(propertyDataDto);
                }
                else
                {
                    int primaryKey = Convert.ToInt32(Database.Insert(propertyDataDto));
                    keyDictionary.Add(propertyDataDto.PropertyTypeId, primaryKey);
                }
            }

            //Update Properties with its newly set Id
            if (keyDictionary.Any())
            {
                foreach (var property in entity.Properties)
                {
                    if (keyDictionary.ContainsKey(property.PropertyTypeId) == false) continue;

                    property.Id = keyDictionary[property.PropertyTypeId];
                }
            }

            // tags:
            if (HasTagProperty(entity))
            {
                // if path-published, update tags, else clear tags
                switch (content.PublishedState)
                {
                    case PublishedState.Publishing:
                        // explicitely publishing, must update tags
                        UpdateEntityTags(entity, _tagRepository);
                        break;
                    case PublishedState.Unpublishing:
                        // explicitely unpublishing, must clear tags
                        ClearEntityTags(entity, _tagRepository);
                        break;
                    case PublishedState.Saving:
                        // saving, nothing to do
                        break;
                    case PublishedState.Published:
                    case PublishedState.Unpublished:
                        // no change, depends on path-published
                        // that should take care of trashing and un-trashing
                        if (IsPathPublished(entity)) // slightly expensive ;-(
                            UpdateEntityTags(entity, _tagRepository);
                        else
                            ClearEntityTags(entity, _tagRepository);
                        break;
                }
            }

            // published => update published version infos,
            // else if unpublished then clear published version infos
            // else leave unchanged
            if (entity.Published)
            {
                dto.DocumentPublishedReadOnlyDto = new DocumentPublishedReadOnlyDto
                {
                    VersionId = dto.VersionId,
                    Newest = true,
                    NodeId = dto.NodeId,
                    Published = true
                };
                content.PublishedVersionGuid = dto.VersionId;
            }
            else if (publishedStateChanged)
            {
                dto.DocumentPublishedReadOnlyDto = new DocumentPublishedReadOnlyDto
                {
                    VersionId = default(Guid),
                    Newest = false,
                    NodeId = dto.NodeId,
                    Published = false
                };
                content.PublishedVersionGuid = default(Guid);
            }

            OnUowRefreshedEntity(new UnitOfWorkEntityEventArgs(UnitOfWork, entity));

            entity.ResetDirtyProperties();
        }

        private int NextChildSortOrder(int parentId)
        {
            var maxSortOrder =
                Database.ExecuteScalar<int>(
                    "SELECT coalesce(max(sortOrder),0) FROM umbracoNode WHERE parentid = @ParentId AND nodeObjectType = @NodeObjectType",
                    new { ParentId = parentId, NodeObjectType = NodeObjectTypeId });
            return maxSortOrder + 1;
        }

        #endregion

        #region Implementation of IContentRepository

        public IEnumerable<IContent> GetByPublishedVersion(IQuery<IContent> query)
        {
            // we WANT to return contents in top-down order, ie parents should come before children
            // ideal would be pure xml "document order" - which we cannot achieve at database level

            var sqlClause = GetBaseQuery(false);
            var translator = new SqlTranslator<IContent>(sqlClause, query);
            var sql = translator.Translate()
                                .Where<DocumentDto>(x => x.Published)
                                .OrderBy<NodeDto>(x => x.Level)
                                .OrderBy<NodeDto>(x => x.SortOrder);

            //NOTE: This doesn't allow properties to be part of the query
            var dtos = Database.Fetch<DocumentDto>(sql);

            foreach (var dto in dtos)
            {
                // check cache first, if it exists and is published, use it
                // it may exist and not be published as the cache has 'latest version used'
                var fromCache = RuntimeCache.GetCacheItem<IContent>(GetCacheIdKey<IContent>(dto.NodeId));
                yield return fromCache != null && fromCache.Published
                    ? fromCache
                    : CreateContentFromDto(dto, dto.VersionId);
            }
        }

        public int CountPublished(string contentTypeAlias = null)
        {
            var sql = Sql();
            if (contentTypeAlias.IsNullOrWhiteSpace())
            {
                sql.SelectCount()
                    .From<NodeDto>()
                    .InnerJoin<DocumentDto>()
                    .On<NodeDto, DocumentDto>(left => left.NodeId, right => right.NodeId)
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId && x.Trashed == false)
                    .Where<DocumentDto>(x => x.Published);
            }
            else
            {
                sql.SelectCount()
                    .From<NodeDto>()
                    .InnerJoin<ContentDto>()
                    .On<NodeDto, ContentDto>(left => left.NodeId, right => right.NodeId)
                    .InnerJoin<DocumentDto>()
                    .On<NodeDto, DocumentDto>(left => left.NodeId, right => right.NodeId)
                    .InnerJoin<ContentTypeDto>()
                    .On<ContentTypeDto, ContentDto>(left => left.NodeId, right => right.ContentTypeId)
                    .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId && x.Trashed == false)
                    .Where<ContentTypeDto>(x => x.Alias == contentTypeAlias)
                    .Where<DocumentDto>(x => x.Published);
            }

            return Database.ExecuteScalar<int>(sql);
        }

        public void ReplaceContentPermissions(EntityPermissionSet permissionSet)
        {
            var repo = new PermissionRepository<IContent>(UnitOfWork, _cacheHelper);
            repo.ReplaceEntityPermissions(permissionSet);
        }

        public void ClearPublishedFlag(IContent content)
        {
            // no race cond if locked
            var documentDtos = Database.Fetch<DocumentDto>("WHERE nodeId=@Id AND published=@IsPublished", new { /*Id =*/ content.Id, IsPublished = true });
            foreach (var documentDto in documentDtos)
            {
                documentDto.Published = false;
                Database.Update(documentDto);
            }
        }

        public void ClearNewestFlag(IContent content)
        {
            // no race cond if locked
            var documentDtos = Database.Fetch<DocumentDto>("WHERE nodeId=@Id AND newest=@IsNewest", new { /*Id =*/ content.Id, IsNewest = true });
            foreach (var documentDto in documentDtos)
            {
                documentDto.Newest = false;
                Database.Update(documentDto);
            }
        }

        /// <summary>
        /// Assigns a single permission to the current content item for the specified user ids
        /// </summary>
        /// <param name="entity"></param>
        /// <param name="permission"></param>
        /// <param name="userIds"></param>
        public void AssignEntityPermission(IContent entity, char permission, IEnumerable<int> userIds)
        {
            var repo = new PermissionRepository<IContent>(UnitOfWork, _cacheHelper);
            repo.AssignEntityPermission(entity, permission, userIds);
        }

        public IEnumerable<EntityPermission> GetPermissionsForEntity(int entityId)
        {
            var repo = new PermissionRepository<IContent>(UnitOfWork, _cacheHelper);
            return repo.GetPermissionsForEntity(entityId);
        }

        /// <summary>
        /// Gets paged content results
        /// </summary>
        /// <param name="query">Query to excute</param>
        /// <param name="pageIndex">Page number</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="totalRecords">Total records query would return without paging</param>
        /// <param name="orderBy">Field to order by</param>
        /// <param name="orderDirection">Direction to order by</param>
        /// <param name="orderBySystemField">Flag to indicate when ordering by system field</param>
        /// <param name="filter"></param>
        /// <returns>An Enumerable list of <see cref="IContent"/> objects</returns>
        public IEnumerable<IContent> GetPagedResultsByQuery(IQuery<IContent> query, long pageIndex, int pageSize, out long totalRecords,
            string orderBy, Direction orderDirection, bool orderBySystemField, IQuery<IContent> filter = null, bool newest = true)
        {
            var filterSql = Sql();
            if (newest)
                filterSql.Append("AND (cmsDocument.newest = 1)");

            if (filter != null)
            {
                foreach (var filterClause in filter.GetWhereClauses())
                    filterSql.Append($"AND ({filterClause.Item1})", filterClause.Item2);
            }

            return GetPagedResultsByQuery<DocumentDto>(query, pageIndex, pageSize, out totalRecords,
                MapQueryDtos,
                orderBy, orderDirection, orderBySystemField,
                filterSql);
        }

        public bool IsPathPublished(IContent content)
        {
            // fail fast
            if (content.Path.StartsWith("-1,-20,"))
                return false;
            // succeed fast
            if (content.ParentId == -1)
                return content.HasPublishedVersion;

            var syntaxUmbracoNode = SqlSyntax.GetQuotedTableName("umbracoNode");
            var syntaxPath = SqlSyntax.GetQuotedColumnName("path");
            var syntaxConcat = SqlSyntax.GetConcat(syntaxUmbracoNode + "." + syntaxPath, "',%'");

            var sql = string.Format(@"SELECT COUNT({0}.{1})
FROM {0}
JOIN {2} ON ({0}.{1}={2}.{3} AND {2}.{4}=@published)
WHERE (@path LIKE {5})",
                syntaxUmbracoNode,
                SqlSyntax.GetQuotedColumnName("id"),
                SqlSyntax.GetQuotedTableName("cmsDocument"),
                SqlSyntax.GetQuotedColumnName("nodeId"),
                SqlSyntax.GetQuotedColumnName("published"),
                syntaxConcat);

            var count = Database.ExecuteScalar<int>(sql, new { @published=true, @path=content.Path });
            count += 1; // because content does not count
            return count == content.Level;
        }

        #endregion

        #region IRecycleBinRepository members

        protected override int RecycleBinId => Constants.System.RecycleBinContent;

        #endregion

        protected override string GetDatabaseFieldNameForOrderBy(string orderBy)
        {
            // NOTE see sortby.prevalues.controller.js for possible values
            // that need to be handled here or in VersionableRepositoryBase

            //Some custom ones
            switch (orderBy.ToUpperInvariant())
            {
                case "UPDATER":
                    //TODO: This isn't going to work very nicely because it's going to order by ID, not by letter
                    return GetDatabaseFieldNameForOrderBy("cmsDocument", "documentUser");
                case "PUBLISHED":
                    return GetDatabaseFieldNameForOrderBy("cmsDocument", "published");
                case "CONTENTTYPEALIAS":
                    throw new NotSupportedException("Don't know how to support ContentTypeAlias.");
            }

            return base.GetDatabaseFieldNameForOrderBy(orderBy);
        }

        private IEnumerable<IContent> MapQueryDtos(List<DocumentDto> dtos)
        {
            //nothing found
            if (dtos.Any() == false) return Enumerable.Empty<IContent>();

            //content types
            //NOTE: This should be ok for an SQL 'IN' statement, there shouldn't be an insane amount of content types
            var contentTypes = _contentTypeRepository.GetAll(dtos.Select(x => x.ContentVersionDto.ContentDto.ContentTypeId).ToArray())
                .ToArray();


            var ids = dtos
                .Where(dto => dto.TemplateId.HasValue && dto.TemplateId.Value > 0)
                .Select(x => x.TemplateId.Value).ToArray();

            //NOTE: This should be ok for an SQL 'IN' statement, there shouldn't be an insane amount of content types
            var templates = ids.Length == 0 ? Enumerable.Empty<ITemplate>() : _templateRepository.GetAll(ids).ToArray();

            var dtosWithContentTypes = dtos
                //This select into and null check are required because we don't have a foreign damn key on the contentType column
                // http://issues.umbraco.org/issue/U4-5503
                .Select(x => new { dto = x, contentType = contentTypes.FirstOrDefault(ct => ct.Id == x.ContentVersionDto.ContentDto.ContentTypeId) })
                .Where(x => x.contentType != null)
                .ToArray();

            //Go get the property data for each document
            var docDefs = dtosWithContentTypes.Select(d => new DocumentDefinition(
                d.dto.NodeId,
                d.dto.VersionId,
                d.dto.ContentVersionDto.VersionDate,
                d.dto.ContentVersionDto.ContentDto.NodeDto.CreateDate,
                d.contentType));

            var propertyData = GetPropertyCollection(docDefs.ToArray());

            return dtosWithContentTypes.Select(d => CreateContentFromDto(
                d.dto,
                contentTypes.First(ct => ct.Id == d.dto.ContentVersionDto.ContentDto.ContentTypeId),
                templates.FirstOrDefault(tem => tem.Id == (d.dto.TemplateId ?? -1)),
                propertyData[d.dto.NodeId]));
        }

        /// <summary>
        /// Private method to create a content object from a DocumentDto, which is used by Get and GetByVersion.
        /// </summary>
        /// <param name="dto"></param>
        /// <param name="contentType"></param>
        /// <param name="template"></param>
        /// <param name="propCollection"></param>
        /// <returns></returns>
        private IContent CreateContentFromDto(DocumentDto dto,
            IContentType contentType,
            ITemplate template,
            PropertyCollection propCollection)
        {
            var factory = new ContentFactory(contentType, NodeObjectTypeId, dto.NodeId);
            var content = factory.BuildEntity(dto);

            //Check if template id is set on DocumentDto, and get ITemplate if it is.
            if (dto.TemplateId.HasValue && dto.TemplateId.Value > 0)
            {
                content.Template = template ?? _templateRepository.Get(dto.TemplateId.Value);
            }
            else
            {
                //ensure there isn't one set.
                content.Template = null;
            }

            content.Properties = propCollection;

            //on initial construction we don't want to have dirty properties tracked
            // http://issues.umbraco.org/issue/U4-1946
            ((Entity)content).ResetDirtyProperties(false);
            return content;
        }

        /// <summary>
        /// Private method to create a content object from a DocumentDto, which is used by Get and GetByVersion.
        /// </summary>
        /// <param name="dto"></param>
        /// <param name="versionId"></param>
        /// <returns></returns>
        private IContent CreateContentFromDto(DocumentDto dto, Guid versionId)
        {
            var contentType = _contentTypeRepository.Get(dto.ContentVersionDto.ContentDto.ContentTypeId);

            var factory = new ContentFactory(contentType, NodeObjectTypeId, dto.NodeId);
            var content = factory.BuildEntity(dto);

            //Check if template id is set on DocumentDto, and get ITemplate if it is.
            if (dto.TemplateId.HasValue && dto.TemplateId.Value > 0)
            {
                content.Template = _templateRepository.Get(dto.TemplateId.Value);
            }

            var docDef = new DocumentDefinition(dto.NodeId, versionId, content.UpdateDate, content.CreateDate, contentType);

            var properties = GetPropertyCollection(new[] { docDef });

            content.Properties = properties[dto.NodeId];

            //on initial construction we don't want to have dirty properties tracked
            // http://issues.umbraco.org/issue/U4-1946
            ((Entity)content).ResetDirtyProperties(false);
            return content;
        }

        private string EnsureUniqueNodeName(int parentId, string nodeName, int id = 0)
        {
            if (EnsureUniqueNaming == false)
                return nodeName;

            var sql = Sql()
                .SelectAll()
                .From<NodeDto>()
                .Where<NodeDto>(x => x.NodeObjectType == NodeObjectTypeId && x.ParentId == parentId && x.Text.StartsWith(nodeName));

            int uniqueNumber = 1;
            var currentName = nodeName;

            var dtos = Database.Fetch<NodeDto>(sql);
            if (dtos.Any())
            {
                var results = dtos.OrderBy(x => x.Text, new SimilarNodeNameComparer());
                foreach (var dto in results)
                {
                    if (id != 0 && id == dto.NodeId) continue;

                    if (dto.Text.ToLowerInvariant().Equals(currentName.ToLowerInvariant()))
                    {
                        currentName = nodeName + $" ({uniqueNumber})";
                        uniqueNumber++;
                    }
                }
            }

            return currentName;
        }
    }
}