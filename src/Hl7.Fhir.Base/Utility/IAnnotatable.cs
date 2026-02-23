/*
 * Copyright (c) 2017, Firely (info@fire.ly) and contributors
 * See the file CONTRIBUTORS for details.
 *
 * This file is licensed under the BSD 3-Clause license
 * available at https://raw.githubusercontent.com/FirelyTeam/firely-net-sdk/master/LICENSE
 */

using System;

namespace Hl7.Fhir.Utility
{
    public interface IAnnotatable
    {
        void AddAnnotation(object annotation);

        void RemoveAnnotations(Type type);
    }

    public static class AnnotatableExtensions
    {
        public static void RemoveAnnotations<A>(this IAnnotatable annotatable)
        {
            annotatable.RemoveAnnotations(typeof(A));
        }

        public static void SetAnnotation<A>(this IAnnotatable annotatable, A annotation)
        {
            // Lock on the instance itself rather than a global static lock.
            // This allows parallel processing of different objects while maintaining
            // thread-safety for operations on the same object.
            lock (annotatable)
            {
                annotatable.RemoveAnnotations<A>();
                if (annotation != null)
                    annotatable.AddAnnotation(annotation);
            }
        }
    }
}
