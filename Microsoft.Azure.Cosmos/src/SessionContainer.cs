﻿//------------------------------------------------------------
// Copyright (c) Microsoft Corporation.  All rights reserved.
//------------------------------------------------------------

namespace Microsoft.Azure.Cosmos.Common
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Text;
    using System.Threading;
    using Microsoft.Azure.Cosmos.Core.Trace;
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Collections;

    internal sealed class SessionContainer : ISessionContainer
    {
        private volatile SessionContainerState state;

        public SessionContainer(string hostName)
        {
            this.state = new SessionContainerState(hostName);
        }

        // State may be replaced (from a different thread) during an execution of an instance method so in a straightforward
        // implementation the method may acquire lock on the initial state but release it on an replaced state resulting in an 
        // error. To avoid this situation a method reads state into a local variable then it works only with the variable; also
        // it explicitly passes it to the utility methods. We put all logic in static methods since static methods don't have
        // access to instance members it eliminates the problem of accidental accessing state the second time.
        public void ReplaceCurrrentStateWithStateOf(SessionContainer comrade)
        {
            this.state = comrade.state;
        }

        public string HostName
        {
            get { return this.state.hostName; }
        }

        public string GetSessionToken(string collectionLink)
        {
            return SessionContainer.GetSessionToken(this.state, collectionLink);
        }

        public string ResolveGlobalSessionToken(DocumentServiceRequest request)
        {
            return SessionContainer.ResolveGlobalSessionToken(this.state, request);
        }

        public ISessionToken ResolvePartitionLocalSessionToken(DocumentServiceRequest request, string partitionKeyRangeId)
        {
            return SessionContainer.ResolvePartitionLocalSessionToken(this.state, request, partitionKeyRangeId);
        }

        public void ClearTokenByCollectionFullname(string collectionFullname)
        {
            SessionContainer.ClearTokenByCollectionFullname(this.state, collectionFullname);
        }

        public void ClearTokenByResourceId(string resourceId)
        {
            SessionContainer.ClearTokenByResourceId(this.state, resourceId);
        }

        public void SetSessionToken(string collectionRid, string collectionFullname, INameValueCollection responseHeaders)
        {
            SessionContainer.SetSessionToken(this.state, collectionRid, collectionFullname, responseHeaders);
        }

        public void SetSessionToken(DocumentServiceRequest request, INameValueCollection responseHeaders)
        {
            SessionContainer.SetSessionToken(this.state, request, responseHeaders);
        }

        // used in unit tests to check if two SessionContainer are equal
        // a.MakeSnapshot().Equals(b.MakeSnapshot())
        public object MakeSnapshot()
        {
            return SessionContainer.MakeSnapshot(this.state);
        }

        private static string GetSessionToken(SessionContainerState self, string collectionLink)
        {
            bool arePathSegmentsParsed = PathsHelper.TryParsePathSegments(collectionLink, out bool isFeed, out string resourceTypeString, out string resourceIdOrFullName, out bool isNameBased);

            ConcurrentDictionary<string, ISessionToken> partitionKeyRangeIdToTokenMap = null;

            if (arePathSegmentsParsed)
            {
                ulong? maybeRID = null;

                if (isNameBased)
                {
                    string collectionName = PathsHelper.GetCollectionPath(resourceIdOrFullName);

                    if (self.collectionNameByResourceId.TryGetValue(collectionName, out ulong rid))
                    {
                        maybeRID = rid;
                    }
                }
                else
                {
                    ResourceId resourceId = ResourceId.Parse(resourceIdOrFullName);
                    if (resourceId.DocumentCollection != 0)
                    {
                        maybeRID = resourceId.UniqueDocumentCollectionId;
                    }
                }

                if (maybeRID.HasValue)
                {
                    self.sessionTokensRIDBased.TryGetValue(maybeRID.Value, out partitionKeyRangeIdToTokenMap);
                }
            }

            if (partitionKeyRangeIdToTokenMap == null)
            {
                return string.Empty;
            }

            return SessionContainer.GetSessionTokenString(partitionKeyRangeIdToTokenMap);
        }

        private static string ResolveGlobalSessionToken(SessionContainerState self, DocumentServiceRequest request)
        {
            ConcurrentDictionary<string, ISessionToken> partitionKeyRangeIdToTokenMap = SessionContainer.GetPartitionKeyRangeIdToTokenMap(self, request);
            if (partitionKeyRangeIdToTokenMap != null)
            {
                return SessionContainer.GetSessionTokenString(partitionKeyRangeIdToTokenMap);
            }

            return string.Empty;
        }

        private static ISessionToken ResolvePartitionLocalSessionToken(SessionContainerState self, DocumentServiceRequest request, string partitionKeyRangeId)
        {
            return SessionTokenHelper.ResolvePartitionLocalSessionToken(request, partitionKeyRangeId, SessionContainer.GetPartitionKeyRangeIdToTokenMap(self, request));
        }

        private static void ClearTokenByCollectionFullname(SessionContainerState self, string collectionFullname)
        {
            if (!string.IsNullOrEmpty(collectionFullname))
            {
                string collectionName = PathsHelper.GetCollectionPath(collectionFullname);

                self.rwlock.EnterWriteLock();
                try
                {
                    if (self.collectionNameByResourceId.ContainsKey(collectionName))
                    {
                        ulong rid = self.collectionNameByResourceId[collectionName];
                        self.sessionTokensRIDBased.TryRemove(rid, out ConcurrentDictionary<string, ISessionToken> ignored);
                        self.collectionResourceIdByName.TryRemove(rid, out string ignoreString);
                        self.collectionNameByResourceId.TryRemove(collectionName, out ulong ignoreUlong);
                    }
                }
                finally
                {
                    self.rwlock.ExitWriteLock();
                }
            }
        }

        private static void ClearTokenByResourceId(SessionContainerState self, string resourceId)
        {
            if (!string.IsNullOrEmpty(resourceId))
            {
                ResourceId resource = ResourceId.Parse(resourceId);
                if (resource.DocumentCollection != 0)
                {
                    ulong rid = resource.UniqueDocumentCollectionId;

                    self.rwlock.EnterWriteLock();
                    try
                    {
                        if (self.collectionResourceIdByName.ContainsKey(rid))
                        {
                            string collectionName = self.collectionResourceIdByName[rid];
                            self.sessionTokensRIDBased.TryRemove(rid, out ConcurrentDictionary<string, ISessionToken> ignored);
                            self.collectionResourceIdByName.TryRemove(rid, out string ignoreString);
                            self.collectionNameByResourceId.TryRemove(collectionName, out ulong ignoreUlong);
                        }
                    }
                    finally
                    {
                        self.rwlock.ExitWriteLock();
                    }
                }
            }
        }

        private static void SetSessionToken(SessionContainerState self, string collectionRid, string collectionFullname, INameValueCollection responseHeaders)
        {
            ResourceId resourceId = ResourceId.Parse(collectionRid);
            string collectionName = PathsHelper.GetCollectionPath(collectionFullname);
            string token = responseHeaders[HttpConstants.HttpHeaders.SessionToken];
            if (!string.IsNullOrEmpty(token))
            {
                SessionContainer.SetSessionToken(self, resourceId, collectionName, token);
            }
        }

        private static void SetSessionToken(SessionContainerState self, DocumentServiceRequest request, INameValueCollection responseHeaders)
        {
            string token = responseHeaders[HttpConstants.HttpHeaders.SessionToken];

            if (!string.IsNullOrEmpty(token))
            {
                if (SessionContainer.ShouldUpdateSessionToken(request, responseHeaders, out ResourceId resourceId, out string collectionName))
                {
                    SessionContainer.SetSessionToken(self, resourceId, collectionName, token);
                }
            }
        }

        private static SessionContainerSnapshot MakeSnapshot(SessionContainerState self)
        {
            self.rwlock.EnterReadLock();
            try
            {
                return new SessionContainerSnapshot(self.collectionNameByResourceId, self.collectionResourceIdByName, self.sessionTokensRIDBased);
            }
            finally
            {
                self.rwlock.ExitReadLock();
            }
        }

        private static ConcurrentDictionary<string, ISessionToken> GetPartitionKeyRangeIdToTokenMap(SessionContainerState self, DocumentServiceRequest request)
        {
            ulong? maybeRID = null;

            if (request.IsNameBased)
            {
                string collectionName = PathsHelper.GetCollectionPath(request.ResourceAddress);

                if (self.collectionNameByResourceId.TryGetValue(collectionName, out ulong rid))
                {
                    maybeRID = rid;
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(request.ResourceId))
                {
                    ResourceId resourceId = ResourceId.Parse(request.ResourceId);
                    if (resourceId.DocumentCollection != 0)
                    {
                        maybeRID = resourceId.UniqueDocumentCollectionId;
                    }
                }
            }

            ConcurrentDictionary<string, ISessionToken> partitionKeyRangeIdToTokenMap = null;

            if (maybeRID.HasValue)
            {
                self.sessionTokensRIDBased.TryGetValue(maybeRID.Value, out partitionKeyRangeIdToTokenMap);
            }

            return partitionKeyRangeIdToTokenMap;
        }

        private static void SetSessionToken(SessionContainerState self, ResourceId resourceId, string collectionName, string encodedToken)
        {
            string partitionKeyRangeId;
            ISessionToken token;
            if (VersionUtility.IsLaterThan(HttpConstants.Versions.CurrentVersion, HttpConstants.Versions.v2015_12_16))
            {
                string[] tokenParts = encodedToken.Split(':');
                partitionKeyRangeId = tokenParts[0];
                token = SessionTokenHelper.Parse(tokenParts[1], HttpConstants.Versions.CurrentVersion);
            }
            else
            {
                //todo: elasticcollections remove after first upgrade.
                partitionKeyRangeId = "0";
                token = SessionTokenHelper.Parse(encodedToken, HttpConstants.Versions.CurrentVersion);
            }

            DefaultTrace.TraceVerbose("Update Session token {0} {1} {2}", resourceId.UniqueDocumentCollectionId, collectionName, token);

            bool isKnownCollection = false;

            self.rwlock.EnterReadLock();
            try
            {
                isKnownCollection = self.collectionNameByResourceId.TryGetValue(collectionName, out ulong resolvedCollectionResourceId) &&
                                    self.collectionResourceIdByName.TryGetValue(resourceId.UniqueDocumentCollectionId, out string resolvedCollectionName) &&
                                    resolvedCollectionResourceId == resourceId.UniqueDocumentCollectionId &&
                                    resolvedCollectionName == collectionName;

                if (isKnownCollection)
                {
                    SessionContainer.AddSessionToken(self, resourceId.UniqueDocumentCollectionId, partitionKeyRangeId, token);
                }
            }
            finally
            {
                self.rwlock.ExitReadLock();
            }

            if (!isKnownCollection)
            {
                self.rwlock.EnterWriteLock();
                try
                {
                    if (self.collectionNameByResourceId.TryGetValue(collectionName, out ulong resolvedCollectionResourceId))
                    {
                        self.sessionTokensRIDBased.TryRemove(resolvedCollectionResourceId, out ConcurrentDictionary<string, ISessionToken> ignored);
                        self.collectionResourceIdByName.TryRemove(resolvedCollectionResourceId, out string ignoreString);
                    }

                    self.collectionNameByResourceId[collectionName] = resourceId.UniqueDocumentCollectionId;
                    self.collectionResourceIdByName[resourceId.UniqueDocumentCollectionId] = collectionName;

                    SessionContainer.AddSessionToken(self, resourceId.UniqueDocumentCollectionId, partitionKeyRangeId, token);
                }
                finally
                {
                    self.rwlock.ExitWriteLock();
                }
            }
        }

        private static void AddSessionToken(SessionContainerState self, ulong rid, string partitionKeyRangeId, ISessionToken token)
        {
            self.sessionTokensRIDBased.AddOrUpdate(
                rid,
                (ridKey) =>
                {
                    ConcurrentDictionary<string, ISessionToken> tokens = new ConcurrentDictionary<string, ISessionToken>();
                    tokens[partitionKeyRangeId] = token;
                    return tokens;
                },
                (ridKey, tokens) =>
                {
                    tokens.AddOrUpdate(
                        partitionKeyRangeId,
                        token,
                        (existingPartitionKeyRangeId, existingToken) => existingToken.Merge(token));
                    return tokens;
                });
        }

        private static string GetSessionTokenString(ConcurrentDictionary<string, ISessionToken> partitionKeyRangeIdToTokenMap)
        {
            if (VersionUtility.IsLaterThan(HttpConstants.Versions.CurrentVersion, HttpConstants.Versions.v2015_12_16))
            {
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, ISessionToken> pair in partitionKeyRangeIdToTokenMap)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append(",");
                    }

                    sb.Append(pair.Key);
                    sb.Append(":");
                    sb.Append(pair.Value.ConvertToString());
                }

                return sb.ToString();
            }
            else
            {
                //todo:elasticcollections remove after first upgrade.
                if (partitionKeyRangeIdToTokenMap.TryGetValue("0", out ISessionToken lsn))
                {
                    return string.Format(CultureInfo.InvariantCulture, "{0}", lsn);
                }

                return string.Empty;
            }
        }

        private static bool AreDictionariesEqual(Dictionary<string, ISessionToken> first, Dictionary<string, ISessionToken> second)
        {
            if (first.Count != second.Count) return false;

            foreach (KeyValuePair<string, ISessionToken> pair in first)
            {
                if (second.TryGetValue(pair.Key, out ISessionToken tokenValue))
                {
                    if (!tokenValue.Equals(pair.Value))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool ShouldUpdateSessionToken(
            DocumentServiceRequest request,
            INameValueCollection responseHeaders,
            out ResourceId resourceId,
            out string collectionName)
        {
            resourceId = null;
            string ownerFullName = responseHeaders[HttpConstants.HttpHeaders.OwnerFullName];
            if (string.IsNullOrEmpty(ownerFullName)) ownerFullName = request.ResourceAddress;

            collectionName = PathsHelper.GetCollectionPath(ownerFullName);
            string resourceIdString;

            if (request.IsNameBased)
            {
                resourceIdString = responseHeaders[HttpConstants.HttpHeaders.OwnerId];
                if (string.IsNullOrEmpty(resourceIdString)) resourceIdString = request.ResourceId;
            }
            else
            {
                resourceIdString = request.ResourceId;
            }

            if (!string.IsNullOrEmpty(resourceIdString))
            {
                resourceId = ResourceId.Parse(resourceIdString);

                if (resourceId.DocumentCollection != 0 &&
                    collectionName != null &&
                    !ReplicatedResourceClient.IsReadingFromMaster(request.ResourceType, request.OperationType))
                {
                    return true;
                }
            }

            return false;
        }

        private sealed class SessionContainerState
        {
            // TODO, devise a mechanism to handle cache coherency during resource id collision
            public readonly string hostName;
            public readonly ReaderWriterLockSlim rwlock = new ReaderWriterLockSlim();
            public readonly ConcurrentDictionary<string, ulong> collectionNameByResourceId = new ConcurrentDictionary<string, ulong>();
            public readonly ConcurrentDictionary<ulong, string> collectionResourceIdByName = new ConcurrentDictionary<ulong, string>();
            // Map of Collection Rid to map of partitionkeyrangeid to SessionToken.
            public readonly ConcurrentDictionary<ulong, ConcurrentDictionary<string, ISessionToken>> sessionTokensRIDBased = new ConcurrentDictionary<ulong, ConcurrentDictionary<string, ISessionToken>>();

            public SessionContainerState(string hostName)
            {
                this.hostName = hostName;
            }

            ~SessionContainerState()
            {
                if (this.rwlock != null)
                {
                    this.rwlock.Dispose();
                }
            }
        }

        private sealed class SessionContainerSnapshot
        {
            private readonly Dictionary<string, ulong> collectionNameByResourceId;
            private readonly Dictionary<ulong, string> collectionResourceIdByName;
            private readonly Dictionary<ulong, Dictionary<string, ISessionToken>> sessionTokensRIDBased;

            public SessionContainerSnapshot(ConcurrentDictionary<string, ulong> collectionNameByResourceId, ConcurrentDictionary<ulong, string> collectionResourceIdByName, ConcurrentDictionary<ulong, ConcurrentDictionary<string, ISessionToken>> sessionTokensRIDBased)
            {
                this.collectionNameByResourceId = new Dictionary<string, ulong>(collectionNameByResourceId);
                this.collectionResourceIdByName = new Dictionary<ulong, string>(collectionResourceIdByName);
                this.sessionTokensRIDBased = new Dictionary<ulong, Dictionary<string, ISessionToken>>();

                foreach (KeyValuePair<ulong, ConcurrentDictionary<string, ISessionToken>> pair in sessionTokensRIDBased)
                {
                    this.sessionTokensRIDBased.Add(pair.Key, new Dictionary<string, ISessionToken>(pair.Value));
                }
            }

            public override int GetHashCode()
            {
                return 1;
            }

            public override bool Equals(object obj)
            {
                if (obj == null || this.GetType() != obj.GetType())
                {
                    return false;
                }

                SessionContainerSnapshot sibling = (SessionContainerSnapshot)obj;

                if (!AreDictionariesEqual(this.collectionNameByResourceId, sibling.collectionNameByResourceId, (x, y) => x == y)) return false;
                if (!AreDictionariesEqual(this.collectionResourceIdByName, sibling.collectionResourceIdByName, (x, y) => x == y)) return false;
                if (!AreDictionariesEqual(this.sessionTokensRIDBased, sibling.sessionTokensRIDBased, (x, y) => AreDictionariesEqual(x, y, (a, b) => a.Equals(b)))) return false;

                return true;
            }

            private static bool AreDictionariesEqual<T, U>(Dictionary<T, U> left, Dictionary<T, U> right, Func<U, U, bool> areEqual)
            {
                if (left.Count != right.Count) return false;

                foreach (T key in left.Keys)
                {
                    if (!right.ContainsKey(key)) return false;
                    if (!areEqual(left[key], right[key])) return false;
                }

                return true;
            }
        }
    }
}