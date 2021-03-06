using System;
using System.Collections.Generic;
using System.Linq;
using SmartStore.Core;
using SmartStore.Core.Caching;
using SmartStore.Core.Data;
using SmartStore.Core.Domain.Customers;
using SmartStore.Core.Domain.Forums;
using SmartStore.Core.Domain.Stores;
using SmartStore.Core.Events;
using SmartStore.Services.Common;
using SmartStore.Services.Customers;
using SmartStore.Services.Messages;

namespace SmartStore.Services.Forums
{
    /// <summary>
    /// Forum service
    /// </summary>
    public partial class ForumService : IForumService
    {
        #region Constants
		
		private const string FORUMGROUP_ALL_KEY = "SmartStore.forumgroup.all-{0}";
        private const string FORUM_ALLBYFORUMGROUPID_KEY = "SmartStore.forum.allbyforumgroupid-{0}";
        private const string FORUMGROUP_PATTERN_KEY = "SmartStore.forumgroup.";
        private const string FORUM_PATTERN_KEY = "SmartStore.forum.";
        
		#endregion

        #region Fields

        private readonly IRepository<ForumGroup> _forumGroupRepository;
        private readonly IRepository<Forum> _forumRepository;
        private readonly IRepository<ForumTopic> _forumTopicRepository;
        private readonly IRepository<ForumPost> _forumPostRepository;
        private readonly IRepository<PrivateMessage> _forumPrivateMessageRepository;
        private readonly IRepository<ForumSubscription> _forumSubscriptionRepository;
        private readonly ForumSettings _forumSettings;
        private readonly IRepository<Customer> _customerRepository;
        private readonly ICacheManager _cacheManager;
        private readonly IGenericAttributeService _genericAttributeService;
        private readonly ICustomerService _customerService;
        private readonly IWorkflowMessageService _workflowMessageService;
		private readonly IRepository<StoreMapping> _storeMappingRepository;
		private readonly ICommonServices _services;

        #endregion

        #region Ctor

        public ForumService(ICacheManager cacheManager,
            IRepository<ForumGroup> forumGroupRepository,
            IRepository<Forum> forumRepository,
            IRepository<ForumTopic> forumTopicRepository,
            IRepository<ForumPost> forumPostRepository,
            IRepository<PrivateMessage> forumPrivateMessageRepository,
            IRepository<ForumSubscription> forumSubscriptionRepository,
            ForumSettings forumSettings,
            IRepository<Customer> customerRepository,
            IGenericAttributeService genericAttributeService,
            ICustomerService customerService,
            IWorkflowMessageService workflowMessageService,
			IRepository<StoreMapping> storeMappingRepository,
			ICommonServices services)
        {
            _cacheManager = cacheManager;
            _forumGroupRepository = forumGroupRepository;
            _forumRepository = forumRepository;
            _forumTopicRepository = forumTopicRepository;
            _forumPostRepository = forumPostRepository;
            _forumPrivateMessageRepository = forumPrivateMessageRepository;
            _forumSubscriptionRepository = forumSubscriptionRepository;
            _forumSettings = forumSettings;
            _customerRepository = customerRepository;
            _genericAttributeService = genericAttributeService;
            _customerService = customerService;
            _workflowMessageService = workflowMessageService;
			_storeMappingRepository = storeMappingRepository;
			_services = services;
        }

		public DbQuerySettings QuerySettings { get; set; }

		#endregion

        #region Utilities

        /// <summary>
        /// Update forum stats
        /// </summary>
        /// <param name="forumId">The forum identifier</param>
        private void UpdateForumStats(int forumId)
        {
            if (forumId == 0)
            {
                return;
            }
            var forum = GetForumById(forumId);
            if (forum == null)
            {
                return;
            }

            //number of topics
            var queryNumTopics = from ft in _forumTopicRepository.Table
                                 where ft.ForumId == forumId
                                 select ft.Id;
            int numTopics = queryNumTopics.Count();

            //number of posts
            var queryNumPosts = from ft in _forumTopicRepository.Table
                                join fp in _forumPostRepository.Table on ft.Id equals fp.TopicId
                                where ft.ForumId == forumId
                                select fp.Id;
            int numPosts = queryNumPosts.Count();

            //last values
            int lastTopicId = 0;
            int lastPostId = 0;
            int lastPostCustomerId = 0;
            DateTime? lastPostTime = null;
            var queryLastValues = from ft in _forumTopicRepository.Table
                                  join fp in _forumPostRepository.Table on ft.Id equals fp.TopicId
                                  where ft.ForumId == forumId
                                  orderby fp.CreatedOnUtc descending, ft.CreatedOnUtc descending
                                  select new
                                  {
                                      LastTopicId = ft.Id,
                                      LastPostId = fp.Id,
                                      LastPostCustomerId = fp.CustomerId,
                                      LastPostTime = fp.CreatedOnUtc
                                  };
            var lastValues = queryLastValues.FirstOrDefault();
            if (lastValues != null)
            {
                lastTopicId = lastValues.LastTopicId;
                lastPostId = lastValues.LastPostId;
                lastPostCustomerId = lastValues.LastPostCustomerId;
                lastPostTime = lastValues.LastPostTime;
            }

            //update forum
            forum.NumTopics = numTopics;
            forum.NumPosts = numPosts;
            forum.LastTopicId = lastTopicId;
            forum.LastPostId = lastPostId;
            forum.LastPostCustomerId = lastPostCustomerId;
            forum.LastPostTime = lastPostTime;
            UpdateForum(forum);
        }

        /// <summary>
        /// Update forum topic stats
        /// </summary>
        /// <param name="forumTopicId">The forum topic identifier</param>
        private void UpdateForumTopicStats(int forumTopicId)
        {
            if (forumTopicId == 0)
            {
                return;
            }
            var forumTopic = GetTopicById(forumTopicId);
            if (forumTopic == null)
            {
                return;
            }

            //number of posts
            var queryNumPosts = from fp in _forumPostRepository.Table
                                where fp.TopicId == forumTopicId
                                select fp.Id;
            int numPosts = queryNumPosts.Count();

            //last values
            int lastPostId = 0;
            int lastPostCustomerId = 0;
            DateTime? lastPostTime = null;
            var queryLastValues = from fp in _forumPostRepository.Table
                                  where fp.TopicId == forumTopicId
                                  orderby fp.CreatedOnUtc descending
                                  select new
                                  {
                                      LastPostId = fp.Id,
                                      LastPostCustomerId = fp.CustomerId,
                                      LastPostTime = fp.CreatedOnUtc
                                  };
            var lastValues = queryLastValues.FirstOrDefault();
            if (lastValues != null)
            {
                lastPostId = lastValues.LastPostId;
                lastPostCustomerId = lastValues.LastPostCustomerId;
                lastPostTime = lastValues.LastPostTime;
            }

            //update topic
            forumTopic.NumPosts = numPosts;
            forumTopic.LastPostId = lastPostId;
            forumTopic.LastPostCustomerId = lastPostCustomerId;
            forumTopic.LastPostTime = lastPostTime;
            UpdateTopic(forumTopic);
        }

        /// <summary>
        /// Update customer stats
        /// </summary>
        /// <param name="customerId">The customer identifier</param>
        private void UpdateCustomerStats(int customerId)
        {
            if (customerId == 0)
            {
                return;
            }

            var customer = _customerService.GetCustomerById(customerId);

            if (customer == null)
            {
                return;
            }

            var query = from fp in _forumPostRepository.Table
                        where fp.CustomerId == customerId
                        select fp.Id;
            int numPosts = query.Count();

            _genericAttributeService.SaveAttribute(customer, SystemCustomerAttributeNames.ForumPostCount, numPosts);
        }

        private bool IsForumModerator(Customer customer)
        {
            if (customer.IsForumModerator())
            {
                return true;
            }
            return false;
        }

        #endregion

        #region Methods

        /// <summary>
        /// Deletes a forum group
        /// </summary>
        /// <param name="forumGroup">Forum group</param>
        public virtual void DeleteForumGroup(ForumGroup forumGroup)
        {
            if (forumGroup == null)
            {
                throw new ArgumentNullException("forumGroup");
            }

            _forumGroupRepository.Delete(forumGroup);

            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityDeleted(forumGroup);
        }

        /// <summary>
        /// Gets a forum group
        /// </summary>
        /// <param name="forumGroupId">The forum group identifier</param>
        /// <returns>Forum group</returns>
        public virtual ForumGroup GetForumGroupById(int forumGroupId)
        {
            if (forumGroupId == 0)
                return null;

            return _forumGroupRepository.GetById(forumGroupId);
        }

        /// <summary>
        /// Gets all forum groups
        /// </summary>
        /// <returns>Forum groups</returns>
		public virtual IList<ForumGroup> GetAllForumGroups(bool showHidden = false)
        {
			string key = string.Format(FORUMGROUP_ALL_KEY, showHidden);
            return _cacheManager.Get(key, () =>
            {
				var query = _forumGroupRepository.Table;

				if (!showHidden && !QuerySettings.IgnoreMultiStore)
				{
					var currentStoreId = _services.StoreContext.CurrentStore.Id;

					query = 
						from fg in query
						join sm in _storeMappingRepository.Table on new { c1 = fg.Id, c2 = "ForumGroup" } equals new { c1 = sm.EntityId, c2 = sm.EntityName } into fg_sm
						from sm in fg_sm.DefaultIfEmpty()
						where !fg.LimitedToStores || currentStoreId == sm.StoreId
						select fg;

					query =
						from fg in query
						group fg by fg.Id into fgGroup
						orderby fgGroup.Key
						select fgGroup.FirstOrDefault();
				}

				query = query.OrderBy(m => m.DisplayOrder);

                return query.ToList();
            });
        }

        /// <summary>
        /// Inserts a forum group
        /// </summary>
        /// <param name="forumGroup">Forum group</param>
        public virtual void InsertForumGroup(ForumGroup forumGroup)
        {
            if (forumGroup == null)
            {
                throw new ArgumentNullException("forumGroup");
            }

            _forumGroupRepository.Insert(forumGroup);

            //cache
            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityInserted(forumGroup);
        }

        /// <summary>
        /// Updates the forum group
        /// </summary>
        /// <param name="forumGroup">Forum group</param>
        public virtual void UpdateForumGroup(ForumGroup forumGroup)
        {
            if (forumGroup == null)
            {
                throw new ArgumentNullException("forumGroup");
            }

            _forumGroupRepository.Update(forumGroup);

            //cache
            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityUpdated(forumGroup);
        }

        /// <summary>
        /// Deletes a forum
        /// </summary>
        /// <param name="forum">Forum</param>
        public virtual void DeleteForum(Forum forum)
        {            
            if (forum == null)
            {
                throw new ArgumentNullException("forum");
            }

            //delete forum subscriptions (topics)
            var queryTopicIds = from ft in _forumTopicRepository.Table
                           where ft.ForumId == forum.Id
                           select ft.Id;
            var queryFs1 = from fs in _forumSubscriptionRepository.Table
                           where queryTopicIds.Contains(fs.TopicId)
                           select fs;
            foreach (var fs in queryFs1.ToList())
            {
                _forumSubscriptionRepository.Delete(fs);
                //event notification
                _services.EventPublisher.EntityDeleted(fs);
            }

            //delete forum subscriptions (forum)
            var queryFs2 = from fs in _forumSubscriptionRepository.Table
                           where fs.ForumId == forum.Id
                           select fs;
            foreach (var fs2 in queryFs2.ToList())
            {
                _forumSubscriptionRepository.Delete(fs2);
                //event notification
                _services.EventPublisher.EntityDeleted(fs2);
            }

            //delete forum
            _forumRepository.Delete(forum);

            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityDeleted(forum);
        }

        /// <summary>
        /// Gets a forum
        /// </summary>
        /// <param name="forumId">The forum identifier</param>
        /// <returns>Forum</returns>
        public virtual Forum GetForumById(int forumId)
        {
            if (forumId == 0)
                return null;

            return _forumRepository.GetById(forumId);
        }

        /// <summary>
        /// Gets forums by forum group identifier
        /// </summary>
        /// <param name="forumGroupId">The forum group identifier</param>
        /// <returns>Forums</returns>
        public virtual IList<Forum> GetAllForumsByGroupId(int forumGroupId)
        {
            string key = string.Format(FORUM_ALLBYFORUMGROUPID_KEY, forumGroupId);
            return _cacheManager.Get(key, () =>
            {
                var query = from f in _forumRepository.Table
                            orderby f.DisplayOrder
                            where f.ForumGroupId == forumGroupId
                            select f;
                var forums = query.ToList();
                return forums;
            });
        }

        /// <summary>
        /// Inserts a forum
        /// </summary>
        /// <param name="forum">Forum</param>
        public virtual void InsertForum(Forum forum)
        {
            if (forum == null)
            {
                throw new ArgumentNullException("forum");
            }

            _forumRepository.Insert(forum);

            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityInserted(forum);
        }

        /// <summary>
        /// Updates the forum
        /// </summary>
        /// <param name="forum">Forum</param>
        public virtual void UpdateForum(Forum forum)
        {
            if (forum == null)
            {
                throw new ArgumentNullException("forum");
            }

            _forumRepository.Update(forum);
            
            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityUpdated(forum);
        }

        /// <summary>
        /// Deletes a forum topic
        /// </summary>
        /// <param name="forumTopic">Forum topic</param>
        public virtual void DeleteTopic(ForumTopic forumTopic)
        {            
            if (forumTopic == null)
            {                
                throw new ArgumentNullException("forumTopic");
            }                

            int customerId = forumTopic.CustomerId;
            int forumId = forumTopic.ForumId;

            //delete topic
            _forumTopicRepository.Delete(forumTopic);

            //delete forum subscriptions
            var queryFs = from ft in _forumSubscriptionRepository.Table
                          where ft.TopicId == forumTopic.Id
                          select ft;
            var forumSubscriptions = queryFs.ToList();
            foreach (var fs in forumSubscriptions)
            {
                _forumSubscriptionRepository.Delete(fs);
                //event notification
                _services.EventPublisher.EntityDeleted(fs);
            }

            //update stats
            UpdateForumStats(forumId);
            UpdateCustomerStats(customerId);

            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityDeleted(forumTopic);
        }

        /// <summary>
        /// Gets a forum topic
        /// </summary>
        /// <param name="forumTopicId">The forum topic identifier</param>
        /// <returns>Forum Topic</returns>
        public virtual ForumTopic GetTopicById(int forumTopicId)
        {
            return GetTopicById(forumTopicId, false);
        }

        /// <summary>
        /// Gets a forum topic
        /// </summary>
        /// <param name="forumTopicId">The forum topic identifier</param>
        /// <param name="increaseViews">The value indicating whether to increase forum topic views</param>
        /// <returns>Forum Topic</returns>
        public virtual ForumTopic GetTopicById(int forumTopicId, bool increaseViews)
        {
            if (forumTopicId == 0)
            {
                return null;
            }

            var query = from ft in _forumTopicRepository.Table
                        where ft.Id == forumTopicId
                        select ft;
            var forumTopic = query.SingleOrDefault();
            if (forumTopic == null)
            {
                return null;
            }

            if (increaseViews)
            {
                forumTopic.Views = ++forumTopic.Views;
                UpdateTopic(forumTopic);
            }

            return forumTopic;
        }

        /// <summary>
        /// Gets all forum topics
        /// </summary>
        /// <param name="forumId">The forum identifier</param>
        /// <param name="customerId">The customer identifier</param>
        /// <param name="keywords">Keywords</param>
        /// <param name="searchType">Search type</param>
        /// <param name="limitDays">Limit by the last number days; 0 to load all topics</param>
        /// <param name="pageIndex">Page index</param>
        /// <param name="pageSize">Page size</param>
        /// <returns>Forum Topics</returns>
        public virtual IPagedList<ForumTopic> GetAllTopics(int forumId, int customerId, string keywords, ForumSearchType searchType, int limitDays, int pageIndex, int pageSize)
        {
            DateTime? limitDate = null;

            if (limitDays > 0)
            {
                limitDate = DateTime.UtcNow.AddDays(-limitDays);
            }

            var query1 = 
				from ft in _forumTopicRepository.Table
				join fp in _forumPostRepository.Table on ft.Id equals fp.TopicId
				where
					(forumId == 0 || ft.ForumId == forumId) &&
					(customerId == 0 || ft.CustomerId == customerId) &&
					(!limitDate.HasValue || limitDate.Value <= ft.LastPostTime) &&
					(
						((searchType == ForumSearchType.All || searchType == ForumSearchType.TopicTitlesOnly) && ft.Subject.Contains(keywords)) ||
						((searchType == ForumSearchType.All || searchType == ForumSearchType.PostTextOnly) && fp.Text.Contains(keywords))
					)							
				select ft.Id;

            var query2 = 
				from ft in _forumTopicRepository.Table
				where query1.Contains(ft.Id)
				orderby ft.TopicTypeId descending, ft.LastPostTime descending, ft.Id descending
				select ft;

            var topics = new PagedList<ForumTopic>(query2, pageIndex, pageSize);
            return topics;
        }

        /// <summary>
        /// Gets active forum topics
        /// </summary>
        /// <param name="forumId">The forum identifier</param>
        /// <param name="count">Count of forum topics to return</param>
        /// <returns>Forum Topics</returns>
        public virtual IList<ForumTopic> GetActiveTopics(int forumId, int count)
        {
            var query =
				from ft in _forumTopicRepository.Table
				where (forumId == 0 || ft.ForumId == forumId) && (ft.LastPostTime.HasValue)
				select ft;

			if (!QuerySettings.IgnoreMultiStore)
			{
				var currentStoreId = _services.StoreContext.CurrentStore.Id;

				query =
					from ft in query
					join ff in _forumRepository.Table on ft.ForumId equals ff.Id
					join fg in _forumGroupRepository.Table on ff.ForumGroupId equals fg.Id
					join sm in _storeMappingRepository.Table on new { c1 = fg.Id, c2 = "ForumGroup" } equals new { c1 = sm.EntityId, c2 = sm.EntityName } into fg_sm
					from sm in fg_sm.DefaultIfEmpty()
					where !fg.LimitedToStores || currentStoreId == sm.StoreId
					select ft;

				query =
					from ft in query
					group ft by ft.Id into ftGroup
					orderby ftGroup.Key
					select ftGroup.FirstOrDefault();
			}

			query = query.OrderByDescending(x => x.LastPostTime);

            var forumTopics = query.Take(count).ToList();
            return forumTopics;
        }

        /// <summary>
        /// Inserts a forum topic
        /// </summary>
        /// <param name="forumTopic">Forum topic</param>
        /// <param name="sendNotifications">A value indicating whether to send notifications to subscribed customers</param>
        public virtual void InsertTopic(ForumTopic forumTopic, bool sendNotifications)
        {
            if (forumTopic == null)
            {
                throw new ArgumentNullException("forumTopic");
            }

            _forumTopicRepository.Insert(forumTopic);

            //update stats
            UpdateForumStats(forumTopic.ForumId);

            //cache            
            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityInserted(forumTopic);

            //send notifications
            if (sendNotifications)
            {
                var forum = forumTopic.Forum;
                var subscriptions = GetAllSubscriptions(0, forum.Id, 0, 0, int.MaxValue);
                var languageId = _services.WorkContext.WorkingLanguage.Id;

                foreach (var subscription in subscriptions)
                {
                    if (subscription.CustomerId == forumTopic.CustomerId)
                    {
                        continue;
                    }

                    if (!String.IsNullOrEmpty(subscription.Customer.Email))
                    {
                        _workflowMessageService.SendNewForumTopicMessage(subscription.Customer, forumTopic, forum, languageId);
                    }
                }
            }
        }

        /// <summary>
        /// Updates the forum topic
        /// </summary>
        /// <param name="forumTopic">Forum topic</param>
        public virtual void UpdateTopic(ForumTopic forumTopic)
        {
            if (forumTopic == null)
            {
                throw new ArgumentNullException("forumTopic");
            }

            _forumTopicRepository.Update(forumTopic);

            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityUpdated(forumTopic);
        }

        /// <summary>
        /// Moves the forum topic
        /// </summary>
        /// <param name="forumTopicId">The forum topic identifier</param>
        /// <param name="newForumId">New forum identifier</param>
        /// <returns>Moved forum topic</returns>
        public virtual ForumTopic MoveTopic(int forumTopicId, int newForumId)
        {
            var forumTopic = GetTopicById(forumTopicId);
            if (forumTopic == null)
                return forumTopic;

            if (this.IsCustomerAllowedToMoveTopic(_services.WorkContext.CurrentCustomer, forumTopic))
            {
                int previousForumId = forumTopic.ForumId;
                var newForum = GetForumById(newForumId);

                if (newForum != null)
                {
                    if (previousForumId != newForumId)
                    {
                        forumTopic.ForumId = newForum.Id;
                        forumTopic.UpdatedOnUtc = DateTime.UtcNow;
                        UpdateTopic(forumTopic);

                        //update forum stats
                        UpdateForumStats(previousForumId);
                        UpdateForumStats(newForumId);
                    }
                }
            }
            return forumTopic;
        }

        /// <summary>
        /// Deletes a forum post
        /// </summary>
        /// <param name="forumPost">Forum post</param>
        public virtual void DeletePost(ForumPost forumPost)
        {            
            if (forumPost == null)
            {
                throw new ArgumentNullException("forumPost");
            }

            int forumTopicId = forumPost.TopicId;
            int customerId = forumPost.CustomerId;
            var forumTopic = this.GetTopicById(forumTopicId);
            int forumId = forumTopic.ForumId;

            //delete topic if it was the first post
            bool deleteTopic = false;
            var firstPost = forumTopic.GetFirstPost(this);
            if (firstPost != null && firstPost.Id == forumPost.Id)
            {
                deleteTopic = true;
            }

            //delete forum post
            _forumPostRepository.Delete(forumPost);

            //delete topic
            if (deleteTopic)
            {
                DeleteTopic(forumTopic);
            }

            //update stats
            if (!deleteTopic)
            {
                UpdateForumTopicStats(forumTopicId);
            }
            UpdateForumStats(forumId);
            UpdateCustomerStats(customerId);

            //clear cache            
            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityDeleted(forumPost);

        }

        /// <summary>
        /// Gets a forum post
        /// </summary>
        /// <param name="forumPostId">The forum post identifier</param>
        /// <returns>Forum Post</returns>
        public virtual ForumPost GetPostById(int forumPostId)
        {
            if (forumPostId == 0)
            {
                return null;
            }

            var query = from fp in _forumPostRepository.Table
                        where fp.Id == forumPostId
                        select fp;
            var forumPost = query.SingleOrDefault();

            return forumPost;
        }

        /// <summary>
        /// Gets all forum posts
        /// </summary>
        /// <param name="forumTopicId">The forum topic identifier</param>
        /// <param name="customerId">The customer identifier</param>
        /// <param name="keywords">Keywords</param>
        /// <param name="pageIndex">Page index</param>
        /// <param name="pageSize">Page size</param>
        /// <returns>Posts</returns>
        public virtual IPagedList<ForumPost> GetAllPosts(int forumTopicId, int customerId, string keywords, int pageIndex, int pageSize)
        {
            return GetAllPosts(forumTopicId, customerId, keywords, true, pageIndex, pageSize);
        }

        /// <summary>
        /// Gets all forum posts
        /// </summary>
        /// <param name="forumTopicId">The forum topic identifier</param>
        /// <param name="customerId">The customer identifier</param>
        /// <param name="keywords">Keywords</param>
        /// <param name="ascSort">Sort order</param>
        /// <param name="pageIndex">Page index</param>
        /// <param name="pageSize">Page size</param>
        /// <returns>Forum Posts</returns>
        public virtual IPagedList<ForumPost> GetAllPosts(int forumTopicId, int customerId, string keywords, bool ascSort, int pageIndex, int pageSize)
        {
            var query = _forumPostRepository.Table;
            if (forumTopicId > 0)
            {
                query = query.Where(fp => forumTopicId == fp.TopicId);
            }
            if (customerId > 0)
            {
                query = query.Where(fp => customerId == fp.CustomerId);
            }
            if (!String.IsNullOrEmpty(keywords))
            {
                query = query.Where(fp => fp.Text.Contains(keywords));
            }
            if (ascSort)
            {
                query = query.OrderBy(fp => fp.CreatedOnUtc).ThenBy(fp => fp.Id);
            }
            else
            {
                query = query.OrderByDescending(fp => fp.CreatedOnUtc).ThenBy(fp => fp.Id);
            }

            var forumPosts = new PagedList<ForumPost>(query, pageIndex, pageSize);

            return forumPosts;
        }

        /// <summary>
        /// Inserts a forum post
        /// </summary>
        /// <param name="forumPost">The forum post</param>
        /// <param name="sendNotifications">A value indicating whether to send notifications to subscribed customers</param>
        public virtual void InsertPost(ForumPost forumPost, bool sendNotifications)
        {
            if (forumPost == null)
            {
                throw new ArgumentNullException("forumPost");
            }

            _forumPostRepository.Insert(forumPost);

            //update stats
            int customerId = forumPost.CustomerId;
            var forumTopic = this.GetTopicById(forumPost.TopicId);
            int forumId = forumTopic.ForumId;
            UpdateForumTopicStats(forumPost.TopicId);
            UpdateForumStats(forumId);
            UpdateCustomerStats(customerId);

            //clear cache            
            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityInserted(forumPost);

            //notifications
            if (sendNotifications)
            {
                var forum = forumTopic.Forum;
                var subscriptions = GetAllSubscriptions(0, 0, forumTopic.Id, 0, int.MaxValue);

                var languageId = _services.WorkContext.WorkingLanguage.Id;

                int friendlyTopicPageIndex = CalculateTopicPageIndex(forumPost.TopicId, _forumSettings.PostsPageSize > 0 ? _forumSettings.PostsPageSize : 10, forumPost.Id) + 1;

                foreach (ForumSubscription subscription in subscriptions)
                {
                    if (subscription.CustomerId == forumPost.CustomerId)
                    {
                        continue;
                    }

                    if (!String.IsNullOrEmpty(subscription.Customer.Email))
                    {
                        _workflowMessageService.SendNewForumPostMessage(subscription.Customer, forumPost, forumTopic, forum, friendlyTopicPageIndex, languageId);
                    }
                }
            }
        }

        /// <summary>
        /// Updates the forum post
        /// </summary>
        /// <param name="forumPost">Forum post</param>
        public virtual void UpdatePost(ForumPost forumPost)
        {
            //validation
            if (forumPost == null)
            {
                throw new ArgumentNullException("forumPost");
            }

            _forumPostRepository.Update(forumPost);

            _cacheManager.RemoveByPattern(FORUMGROUP_PATTERN_KEY);
            _cacheManager.RemoveByPattern(FORUM_PATTERN_KEY);

            //event notification
            _services.EventPublisher.EntityUpdated(forumPost);
        }

        /// <summary>
        /// Deletes a private message
        /// </summary>
        /// <param name="privateMessage">Private message</param>
        public virtual void DeletePrivateMessage(PrivateMessage privateMessage)
        {
            if (privateMessage == null)
            {
                throw new ArgumentNullException("privateMessage");
            }

            _forumPrivateMessageRepository.Delete(privateMessage);

            //event notification
            _services.EventPublisher.EntityDeleted(privateMessage);
        }

        /// <summary>
        /// Gets a private message
        /// </summary>
        /// <param name="privateMessageId">The private message identifier</param>
        /// <returns>Private message</returns>
        public virtual PrivateMessage GetPrivateMessageById(int privateMessageId)
        {
            if (privateMessageId == 0)
            {
                return null;
            }

            var query = from pm in _forumPrivateMessageRepository.Table
                        where pm.Id == privateMessageId
                        select pm;
            var privateMessage = query.SingleOrDefault();

            return privateMessage;
        }

        /// <summary>
        /// Gets private messages
        /// </summary>
		/// <param name="storeId">The store identifier; pass 0 to load all messages</param>
        /// <param name="fromCustomerId">The customer identifier who sent the message</param>
        /// <param name="toCustomerId">The customer identifier who should receive the message</param>
        /// <param name="isRead">A value indicating whether loaded messages are read. false - to load not read messages only, 1 to load read messages only, null to load all messages</param>
        /// <param name="isDeletedByAuthor">A value indicating whether loaded messages are deleted by author. false - messages are not deleted by author, null to load all messages</param>
        /// <param name="isDeletedByRecipient">A value indicating whether loaded messages are deleted by recipient. false - messages are not deleted by recipient, null to load all messages</param>
        /// <param name="keywords">Keywords</param>
        /// <param name="pageIndex">Page index</param>
        /// <param name="pageSize">Page size</param>
        /// <returns>Private messages</returns>
		public virtual IPagedList<PrivateMessage> GetAllPrivateMessages(int storeId, int fromCustomerId,
            int toCustomerId, bool? isRead, bool? isDeletedByAuthor, bool? isDeletedByRecipient,
            string keywords, int pageIndex, int pageSize)
        {
			var query = _forumPrivateMessageRepository.Table;
			if (storeId > 0)
				query = query.Where(pm => storeId == pm.StoreId);
            if (fromCustomerId > 0)
                query = query.Where(pm => fromCustomerId == pm.FromCustomerId);
            if (toCustomerId > 0)
                query = query.Where(pm => toCustomerId == pm.ToCustomerId);
            if (isRead.HasValue)
                query = query.Where(pm => isRead.Value == pm.IsRead);
            if (isDeletedByAuthor.HasValue)
                query = query.Where(pm => isDeletedByAuthor.Value == pm.IsDeletedByAuthor);
            if (isDeletedByRecipient.HasValue)
                query = query.Where(pm => isDeletedByRecipient.Value == pm.IsDeletedByRecipient);
            if (!String.IsNullOrEmpty(keywords))
            {
                query = query.Where(pm => pm.Subject.Contains(keywords));
                query = query.Where(pm => pm.Text.Contains(keywords));
            }
            query = query.OrderByDescending(pm => pm.CreatedOnUtc);

            var privateMessages = new PagedList<PrivateMessage>(query, pageIndex, pageSize);

            return privateMessages;
        }

        /// <summary>
        /// Inserts a private message
        /// </summary>
        /// <param name="privateMessage">Private message</param>
        public virtual void InsertPrivateMessage(PrivateMessage privateMessage)
        {
            if (privateMessage == null)
            {
                throw new ArgumentNullException("privateMessage");
            }

            _forumPrivateMessageRepository.Insert(privateMessage);

            //event notification
            _services.EventPublisher.EntityInserted(privateMessage);

            var customerTo = _customerService.GetCustomerById(privateMessage.ToCustomerId);
            if (customerTo == null)
            {
                throw new SmartException("Recipient could not be loaded");
            }

			//UI notification
			_genericAttributeService.SaveAttribute(customerTo, SystemCustomerAttributeNames.NotifiedAboutNewPrivateMessages, false, privateMessage.StoreId);

            //Email notification
            if (_forumSettings.NotifyAboutPrivateMessages)
            {
                _workflowMessageService.SendPrivateMessageNotification(customerTo, privateMessage, _services.WorkContext.WorkingLanguage.Id);                
            }
        }

        /// <summary>
        /// Updates the private message
        /// </summary>
        /// <param name="privateMessage">Private message</param>
        public virtual void UpdatePrivateMessage(PrivateMessage privateMessage)
        {
            if (privateMessage == null)
                throw new ArgumentNullException("privateMessage");

            if (privateMessage.IsDeletedByAuthor && privateMessage.IsDeletedByRecipient)
            {
                _forumPrivateMessageRepository.Delete(privateMessage);
                //event notification
                _services.EventPublisher.EntityDeleted(privateMessage);
            }
            else
            {
                _forumPrivateMessageRepository.Update(privateMessage);
                //event notification
                _services.EventPublisher.EntityUpdated(privateMessage);
            }
        }

        /// <summary>
        /// Deletes a forum subscription
        /// </summary>
        /// <param name="forumSubscription">Forum subscription</param>
        public virtual void DeleteSubscription(ForumSubscription forumSubscription)
        {
            if (forumSubscription == null)
            {
                throw new ArgumentNullException("forumSubscription");
            }

            _forumSubscriptionRepository.Delete(forumSubscription);

            //event notification
            _services.EventPublisher.EntityDeleted(forumSubscription);
        }

        /// <summary>
        /// Gets a forum subscription
        /// </summary>
        /// <param name="forumSubscriptionId">The forum subscription identifier</param>
        /// <returns>Forum subscription</returns>
        public virtual ForumSubscription GetSubscriptionById(int forumSubscriptionId)
        {
            if (forumSubscriptionId == 0)
            {
                return null;
            }

            var query = from fs in _forumSubscriptionRepository.Table
                        where fs.Id == forumSubscriptionId
                        select fs;
            var forumSubscription = query.SingleOrDefault();

            return forumSubscription;
        }

        /// <summary>
        /// Gets forum subscriptions
        /// </summary>
        /// <param name="customerId">The customer identifier</param>
        /// <param name="forumId">The forum identifier</param>
        /// <param name="topicId">The topic identifier</param>
        /// <param name="pageIndex">Page index</param>
        /// <param name="pageSize">Page size</param>
        /// <returns>Forum subscriptions</returns>
        public virtual IPagedList<ForumSubscription> GetAllSubscriptions(int customerId, int forumId, int topicId, int pageIndex, int pageSize)
        {
            var fsQuery = 
				from fs in _forumSubscriptionRepository.Table
				join c in _customerRepository.Table on fs.CustomerId equals c.Id
				where
					(customerId == 0 || fs.CustomerId == customerId) &&
					(forumId == 0 || fs.ForumId == forumId) &&
					(topicId == 0 || fs.TopicId == topicId) &&
					(c.Active && !c.Deleted)
				select fs.SubscriptionGuid;

            var query = 
				from fs in _forumSubscriptionRepository.Table
				where fsQuery.Contains(fs.SubscriptionGuid)
				orderby fs.CreatedOnUtc descending, fs.SubscriptionGuid descending
				select fs;

            var forumSubscriptions = new PagedList<ForumSubscription>(query, pageIndex, pageSize);
            return forumSubscriptions;
        }

        /// <summary>
        /// Inserts a forum subscription
        /// </summary>
        /// <param name="forumSubscription">Forum subscription</param>
        public virtual void InsertSubscription(ForumSubscription forumSubscription)
        {
            if (forumSubscription == null)
            {
                throw new ArgumentNullException("forumSubscription");
            }

            _forumSubscriptionRepository.Insert(forumSubscription);

            //event notification
            _services.EventPublisher.EntityInserted(forumSubscription);
        }

        /// <summary>
        /// Updates the forum subscription
        /// </summary>
        /// <param name="forumSubscription">Forum subscription</param>
        public virtual void UpdateSubscription(ForumSubscription forumSubscription)
        {
            if (forumSubscription == null)
            {
                throw new ArgumentNullException("forumSubscription");
            }
            
            _forumSubscriptionRepository.Update(forumSubscription);

            //event notification
            _services.EventPublisher.EntityUpdated(forumSubscription);
        }

        /// <summary>
        /// Check whether customer is allowed to create new topics
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="forum">Forum</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToCreateTopic(Customer customer, Forum forum)
        {
            if (forum == null)
            {
                return false;
            }

            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest() && !_forumSettings.AllowGuestsToCreateTopics)
            {
                return false;
            }

            if (IsForumModerator(customer))
            {
                return true;
            }

            return true;
        }

        /// <summary>
        /// Check whether customer is allowed to edit topic
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="topic">Topic</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToEditTopic(Customer customer, ForumTopic topic)
        {
            if (topic == null)
            {
                return false;
            }

            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest())
            {
                return false;
            }

            if (IsForumModerator(customer))
            {
                return true;
            }

            if (_forumSettings.AllowCustomersToEditPosts)
            {
                bool ownTopic = customer.Id == topic.CustomerId;
                return ownTopic;
            }

            return false;
        }

        /// <summary>
        /// Check whether customer is allowed to move topic
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="topic">Topic</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToMoveTopic(Customer customer, ForumTopic topic)
        {
            if (topic == null)
            {
                return false;
            }

            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest())
            {
                return false;
            }

            if (IsForumModerator(customer))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check whether customer is allowed to delete topic
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="topic">Topic</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToDeleteTopic(Customer customer, ForumTopic topic)
        {
            if (topic == null)
            {
                return false;
            }

            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest())
            {
                return false;
            }

            if (IsForumModerator(customer))
            {
                return true;
            }

            if (_forumSettings.AllowCustomersToDeletePosts)
            {
                bool ownTopic = customer.Id == topic.CustomerId;
                return ownTopic;
            }

            return false;
        }

        /// <summary>
        /// Check whether customer is allowed to create new post
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="topic">Topic</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToCreatePost(Customer customer, ForumTopic topic)
        {
            if (topic == null)
            {
                return false;
            }

            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest() && !_forumSettings.AllowGuestsToCreatePosts)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Check whether customer is allowed to edit post
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="post">Topic</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToEditPost(Customer customer, ForumPost post)
        {
            if (post == null)
            {
                return false;
            }

            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest())
            {
                return false;
            }

            if (IsForumModerator(customer))
            {
                return true;
            }

            if (_forumSettings.AllowCustomersToEditPosts)
            {
                bool ownPost = customer.Id == post.CustomerId;
                return ownPost;
            }

            return false;
        }
        
        /// <summary>
        /// Check whether customer is allowed to delete post
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <param name="post">Topic</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToDeletePost(Customer customer, ForumPost post)
        {
            if (post == null)
            {
                return false;
            }

            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest())
            {
                return false;
            }

            if (IsForumModerator(customer))
            {
                return true;
            }

            if (_forumSettings.AllowCustomersToDeletePosts)
            {
                bool ownPost = customer.Id == post.CustomerId;
                return ownPost;
            }

            return false;
        }

        /// <summary>
        /// Check whether customer is allowed to set topic priority
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToSetTopicPriority(Customer customer)
        {
            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest())
            {
                return false;
            }

            if (IsForumModerator(customer))
            {
                return true;
            }            

            return false;
        }

        /// <summary>
        /// Check whether customer is allowed to watch topics
        /// </summary>
        /// <param name="customer">Customer</param>
        /// <returns>True if allowed, otherwise false</returns>
        public virtual bool IsCustomerAllowedToSubscribe(Customer customer)
        {
            if (customer == null)
            {
                return false;
            }

            if (customer.IsGuest())
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Calculates topic page index by post identifier
        /// </summary>
        /// <param name="forumTopicId">Forum topic identifier</param>
        /// <param name="pageSize">Page size</param>
        /// <param name="postId">Post identifier</param>
        /// <returns>Page index</returns>
        public virtual int CalculateTopicPageIndex(int forumTopicId, int pageSize, int postId)
        {
            int pageIndex = 0;
            var forumPosts = GetAllPosts(forumTopicId, 0, string.Empty, true, 0, int.MaxValue);

            for (int i = 0; i < forumPosts.TotalCount; i++)
            {
                if (forumPosts[i].Id == postId)
                {
                    if (pageSize > 0)
                    {
                        pageIndex = i / pageSize;
                    }
                }
            }

            return pageIndex;
        }

		#endregion
    }
}
