﻿using Autofac;
using Autofac.Core;
using BlackSP.Core;
using BlackSP.Core.Processors;
using BlackSP.Core.Dispatchers;
using BlackSP.Core.Endpoints;
using BlackSP.Core.Sources;
using BlackSP.Core.Models;
using BlackSP.Core.Monitors;
using BlackSP.Core.Partitioners;
using BlackSP.Kernel;
using BlackSP.Kernel.MessageProcessing;
using BlackSP.Kernel.Models;
using BlackSP.Kernel.Serialization;
using BlackSP.Serialization;
using Microsoft.IO;
using System;
using System.Collections.Generic;
using BlackSP.Kernel.Checkpointing;
using BlackSP.Checkpointing.Core;
using BlackSP.Checkpointing.Persistence;
using BlackSP.Checkpointing;
using BlackSP.Core.Coordination;

namespace BlackSP.Infrastructure.Extensions
{
    public static class AutofacVertexExtensions
    {

        /// <summary>
        /// Configure types to use network receiver as one or more message sources.
        /// </summary>
        /// <param name="builder"></param>
        /// <param name="useAsDataSource"></param>
        /// <returns></returns>
        public static ContainerBuilder UseReceiverMessageSource(this ContainerBuilder builder, bool useAsDataSource = true)
        {
            var exposedTypes = new List<Type>() { typeof(IReceiver), typeof(ISource<ControlMessage>) };
            if (useAsDataSource)
            {
                exposedTypes.Add(typeof(ISource<DataMessage>));
            }
            builder.RegisterType<ReceiverMessageSource>().As(exposedTypes.ToArray()).SingleInstance();
            return builder;
        }

        /// <summary>
        /// Configure types for dispatching messages from a worker instance.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ContainerBuilder UseWorkerDispatcher(this ContainerBuilder builder)
        {
            builder.RegisterType<PartitioningMessageDispatcher>().As<IDispatcher<IMessage>, IDispatcher<ControlMessage>, IDispatcher<DataMessage>>().SingleInstance();
            builder.RegisterType<PooledBufferMessageSerializer>().As<IObjectSerializer<IMessage>>();
            builder.RegisterType<MessageHashPartitioner>().As<IPartitioner<IMessage>>();

            return builder;
        }

        /// <summary>
        /// Configure types for dispatching messages from a coordinator instance
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ContainerBuilder UseCoordinatorDispatcher(this ContainerBuilder builder)
        {
            builder.RegisterType<ControlMessageDispatcher>().As<IDispatcher<IMessage>, IDispatcher<ControlMessage>>().SingleInstance();
            builder.RegisterType<PooledBufferMessageSerializer>().As<IObjectSerializer<IMessage>>();
            return builder;
        }

        /// <summary>
        /// Configure streaming input and output endpoint types
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ContainerBuilder UseStreamingEndpoints(this ContainerBuilder builder)
        {
            builder.RegisterType<OutputEndpoint>().AsImplementedInterfaces().AsSelf();
            builder.RegisterType<InputEndpoint>().AsImplementedInterfaces().AsSelf();
            return builder;
        }

        /// <summary>
        /// Configure Protobuf-net as internally used serializer.
        /// </summary>
        /// <param name="builder"></param>
        /// <returns></returns>
        public static ContainerBuilder UseProtobufSerializer(this ContainerBuilder builder)
        {
            builder.RegisterType<ProtobufStreamSerializer>().As<IStreamSerializer>();
            builder.RegisterInstance(new RecyclableMemoryStreamManager()); //register one memorystreampool for all components to share
            return builder;
        }


        public static ContainerBuilder UseStatusMonitors(this ContainerBuilder builder)
        {
            builder.RegisterType<ConnectionMonitor>().AsSelf().SingleInstance();
            return builder;
        }


        public static ContainerBuilder UseStateManagers(this ContainerBuilder builder)
        {
            
            builder.RegisterType<WorkerStateManager>().AsSelf().InstancePerDependency();
            builder.RegisterType<WorkerGraphStateManager>().AsSelf().SingleInstance();
            return builder;
        }
    }
}
