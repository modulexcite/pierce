using Pierce.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Pierce.Net
{
    public class RequestQueue : IRequestQueue
    {
        private readonly BlockingCollection<Request> _cache_queue = new BlockingCollection<Request>();
        private readonly BlockingCollection<Request> _network_queue = new BlockingCollection<Request>();

        private readonly ISet<Request> _requests = new HashSet<Request>();
        private readonly IDictionary<object, List<Request>> _blocked_requests = new Dictionary<object, List<Request>>();

        private readonly ICache _cache;
        private readonly INetwork _network;
        private readonly IResponseDelivery _delivery;

        private int _sequence;

        public RequestQueue(ILogManager log, ICache cache, INetwork network, IResponseDelivery delivery)
        {
            Log = log.GetLogger();
            _cache = cache;
            _network = network;
            _delivery = delivery;

            Task.Factory.StartNew(CacheConsumer);

            Task.Factory.StartNew(NetworkConsumer);
            Task.Factory.StartNew(NetworkConsumer);
            Task.Factory.StartNew(NetworkConsumer);
            Task.Factory.StartNew(NetworkConsumer);
        }

        public ILogger Log { get; private set; }

        public Request Add(Request request)
        {
            lock (_requests)
            {
                _requests.Add(request);
            }

            request.RequestQueue = this;
            request.Sequence = Interlocked.Increment(ref _sequence);
            request.AddMarker("add-to-queue");

            if (!request.ShouldCache)
            {
                _network_queue.Add(request);
                return request;
            }

            lock (_blocked_requests)
            {
                List<Request> list;
                if (_blocked_requests.TryGetValue(request.CacheKey, out list))
                {
                    list = list ?? new List<Request>();
                    list.Add(request);

                    _blocked_requests[request.CacheKey] = list;
                }
                else
                {
                    _blocked_requests[request.CacheKey] = null;
                    _cache_queue.Add(request);
                }                
            }

            return request;
        }

        public void Cancel(Func<Request, bool> filter)
        {
            lock (_requests)
            {
                foreach (var request in _requests.Where(filter))
                {
                    request.Cancel();
                }
            }
        }

        public void Cancel(object tag)
        {
            if (tag == null)
            {
                throw new InvalidOperationException("tag");
            }

            Cancel(request => request.Tag == tag);
        }

        public void Finish(Request request)
        {
            lock (_requests)
            {
                _requests.Remove(request);
            }

            if (request.ShouldCache)
            {
                List<Request> list;

                lock (_blocked_requests)
                {
                    if (_blocked_requests.TryGetValue(request.CacheKey, out list) &&
                        _blocked_requests.Remove(request.CacheKey) &&
                        list != null)
                    {
                        list.ForEach(_cache_queue.Add);
                    }
                }
            }
        }

        private void CacheConsumer()
        {
            foreach (var request in _cache_queue.GetConsumingEnumerable())
            {
                request.AddMarker("cache-queue-dequeue");

                if (request.IsCanceled)
                {
                    request.Finish("cache-discard-canceled");
                    continue;
                }

                CacheEntry entry = _cache[request.CacheKey];
                if (entry == null)
                {
                    request.AddMarker("cache-miss");
                    _network_queue.Add(request);
                    continue;
                }

                if (entry.IsExpired)
                {
                    request.AddMarker("cache-hit-expired");
                    request.CacheEntry = entry;
                    _network_queue.Add(request);
                    continue;
                }

                request.AddMarker("cache-hit");
                var response = request.Parse(new NetworkResponse
                                             {
                    Data = entry.Data,
                    Headers = entry.Headers,
                });

                request.AddMarker("cache-hit-parsed");

                if (!entry.ShouldRefresh)
                {
                    _delivery.PostResponse(request, response);
                }
                else
                {
                    request.AddMarker("cache-hit-refresh-needed");
                    request.CacheEntry = entry;
                    response.IsIntermediate = true;

                    _delivery.PostResponse(request, response, () => _network_queue.Add(request));
                }
            }
        }

        private void NetworkConsumer()
        {
            foreach (var request in _network_queue.GetConsumingEnumerable())
            {
                request.AddMarker("network-dequeue");

                if (request.IsCanceled)
                {
                    request.Finish("network-discard-canceled");
                    continue;
                }

                NetworkResponse network_response = null;

                try
                {
                    network_response = _network.Execute(request);
                    request.AddMarker("network-http-complete");

                    if (network_response.StatusCode == HttpStatusCode.NotModified &&
                        request.ResponseDelievered)
                    {
                        request.Finish("not-modified");
                        continue;
                    }

                    var response = request.Parse(network_response);
                    request.AddMarker("network-parse-complete");

                    if (request.ShouldCache && response.CacheEntry != null)
                    {
                        _cache[request.CacheKey] = response.CacheEntry;
                        request.AddMarker("network-cache-written");
                    }

                    request.ResponseDelievered = true;
                    _delivery.PostResponse(request, response);
                }
                catch (RequestException ex)
                {
                    _delivery.PostException(request, ex);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Unhandled Exception");

                    var exception = new RequestException("Unhandled exception", ex, network_response);
                    _delivery.PostException(request, exception);
                }
            }
        }
    }
}

