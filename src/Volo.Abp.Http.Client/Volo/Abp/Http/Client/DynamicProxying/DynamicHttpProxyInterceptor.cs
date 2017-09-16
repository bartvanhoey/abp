﻿using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Volo.Abp.DependencyInjection;
using Volo.Abp.DynamicProxy;
using Volo.Abp.Threading;

namespace Volo.Abp.Http.Client.DynamicProxying
{
    public class DynamicHttpProxyInterceptor<TService> : AbpInterceptor, ITransientDependency
    {
        private static MethodInfo GenericInterceptAsyncMethod { get; }

        private readonly IDynamicProxyHttpClientFactory _httpClientFactory;
        private readonly IApiDescriptionFinder _apiDescriptionFinder;
        private readonly AbpHttpClientOptions _options;

        static DynamicHttpProxyInterceptor()
        {
            GenericInterceptAsyncMethod = typeof(DynamicHttpProxyInterceptor<TService>)
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
                .First(m => m.Name == nameof(InterceptAsync) && m.IsGenericMethodDefinition);
        }

        public DynamicHttpProxyInterceptor(
            IDynamicProxyHttpClientFactory httpClientFactory,
            IOptions<AbpHttpClientOptions> options,
            IApiDescriptionFinder apiDescriptionFinder)
        {
            _httpClientFactory = httpClientFactory;
            _apiDescriptionFinder = apiDescriptionFinder;
            _options = options.Value;
        }

        public override void Intercept(IAbpMethodInvocation invocation)
        {
            AsyncHelper.RunSync(() => InterceptAsync(invocation));
        }

        public override Task InterceptAsync(IAbpMethodInvocation invocation)
        {
            invocation.ReturnValue = GenericInterceptAsyncMethod
                .MakeGenericMethod(invocation.Method.ReturnType.GenericTypeArguments[0])
                .Invoke(this, new object[] { invocation });

            return Task.CompletedTask;
        }

        private async Task<T> InterceptAsync<T>(IAbpMethodInvocation invocation)
        {
            //TODO: Somehow capture cancellationtoken and pass to other methods...?

            var proxyConfig = GetProxyConfig();
            var actionApiDescription = await _apiDescriptionFinder.FindActionAsync(proxyConfig, invocation.Method);

            using (var client = _httpClientFactory.Create())
            {
                var url = proxyConfig.BaseUrl + UrlBuilder.GenerateUrlWithParameters(actionApiDescription, invocation.ArgumentsDictionary);

                var requestMessage = new HttpRequestMessage(actionApiDescription.GetHttpMethod(), url);
                var response = await client.SendAsync(requestMessage);
                if (!response.IsSuccessStatusCode)
                {
                    throw new AbpException("Remote service returns error!");
                }

                var content = await response.Content.ReadAsStringAsync();

                var result = JsonConvert.DeserializeObject(
                    content,
                    typeof(T),
                    new JsonSerializerSettings
                    {
                        ContractResolver = new CamelCasePropertyNamesContractResolver()
                    });

                return (T)result;
            }
        }

        private DynamicHttpClientProxyConfig GetProxyConfig()
        {
            return _options.HttpClientProxies.GetOrDefault(typeof(TService))
                   ?? throw new AbpException($"Could not get DynamicHttpClientProxyConfig for {typeof(TService).FullName}.");
        }
    }
}