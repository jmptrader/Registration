﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using Expr = System.Linq.Expressions.Expression;

namespace ExcelDna.Registration
{
    // CONSIDER: Can one use an ExpressionVisitor to do these things....?
    public static class ParameterConversionRegistration
    {
        public static IEnumerable<ExcelFunctionRegistration> ProcessParameterConversions(this IEnumerable<ExcelFunctionRegistration> registrations, ParameterConversionConfiguration conversionConfig)
        {
            foreach (var reg in registrations)
            {
                // Keep a list of conversions for each parameter
                // TODO: Prevent having a cycle, but allow arbitrary ordering...?

                var paramsConversions = new List<List<LambdaExpression>>();
                for (int i = 0; i < reg.FunctionLambda.Parameters.Count; i++)
                {
                    var initialParamType = reg.FunctionLambda.Parameters[i].Type;
                    var paramReg = reg.ParameterRegistrations[i];

                    var paramConversions = GetParameterConversions(conversionConfig, initialParamType, paramReg);
                    paramsConversions.Add(paramConversions);
                } // for each parameter

                // Process return conversions
                var returnConversions = GetReturnConversions(conversionConfig, reg.FunctionLambda.ReturnType, reg.ReturnCustomAttributes);

                // Now we apply all the conversions
                ApplyConversions(reg, paramsConversions, returnConversions);

                yield return reg;
            }
        }

        // Should return null if there are no conversions to apply
        static List<LambdaExpression> GetParameterConversions(ParameterConversionConfiguration conversionConfig, Type initialParamType, ExcelParameterRegistration paramReg)
        {
            // paramReg might be modified internally, but won't become a different object
            var paramType = initialParamType; // Might become a different type as we convert

            // Assume most parameters will need no conversion
            List<LambdaExpression> paramConversions = null;

            // Keep an extra list of conversions that have been applied, ensuring that each conversion can be applied at most once.
            var conversionsApplied = new List<ParameterConversion>();

            // Try to repeatedly apply conversions until none are applicable.
            // We add a simple guard to covers for cycles and ill-behaved conversions functions
            // TODO: Improve tracing and log better error
            const int maxConversionDepth = 16;
            var depth = 0;
            while (depth < maxConversionDepth)
            {
                // Get type-specific and global conversions, 
                List<ParameterConversion> typeConversions = conversionConfig.GetParameterConversions(paramType);

                var applied = false;
                // We now have the conversions that might be applied to this type...
                // see if we can find one to be applied (that has not been applied before)
                // Note that convert might also make modifications to the paramReg object...
                foreach (var convert in typeConversions.Except(conversionsApplied))
                {
                    var lambda = convert(paramType, paramReg);
                    if (lambda == null)
                        continue; // Try next conversion for this type

                    // We got one to apply...
                    // Some sanity checks
                    Debug.Assert(lambda.Parameters.Count == 1);
                    Debug.Assert(lambda.ReturnType == paramType || lambda.ReturnType.IsEquivalentTo(paramType));

                    // Check if we need to make a new conversion list
                    if (paramConversions == null)
                        paramConversions = new List<LambdaExpression>();

                    paramConversions.Add(lambda);
                    // Change the Parameter Type to be whatever the conversion function takes us to
                    // for the next round of processing
                    paramType = lambda.Parameters[0].Type;
                    conversionsApplied.Add(convert);
                    applied = true;
                    break;
                }
                if (applied)
                    depth++;
                else
                    break; // None of the conversions were applied - stop trying
            } // while checking types

            return paramConversions;
        }

        static List<LambdaExpression> GetReturnConversions(ParameterConversionConfiguration conversionConfig, Type initialReturnType, List<object> returnCustomAttributes)
        {
            // returnCustomAttributes list might be modified, should not become a different object
            var returnType = initialReturnType; // Might become a different type as we convert

            // Assume most returns will need no conversion
            List<LambdaExpression> returnConversions = null;

            // Keep an extra list of conversions that have been applied, ensuring that each conversion can be applied at most once.
            var conversionsApplied = new List<ReturnConversion>();

            // Try to repeatedly apply conversions until none are applicable.
            // We add a simple guard to covers for cycles and ill-behaved conversions functions
            // TODO: Improve tracing and log better error
            const int maxConversionDepth = 16;
            var depth = 0;
            while (depth < maxConversionDepth)
            {
                List<ReturnConversion> typeConversions = conversionConfig.GetReturnConversions(returnType);

                var applied = false;
                // we have conversions that might be applied to this type...
                // see if we can find one to be applied
                // Note that convert might also make modifications to the return attributes list...
                foreach (var convert in typeConversions.Except(conversionsApplied))
                {
                    var lambda = convert(returnType, returnCustomAttributes);
                    if (lambda == null)
                        continue; // Try next conversion for this type

                    // We got one to apply...
                    // Some sanity checks
                    Debug.Assert(lambda.Parameters.Count == 1);
                    Debug.Assert(lambda.Parameters[0].Type == returnType);

                    // Check if we need to make a new conversion list
                    if (returnConversions == null)
                        returnConversions = new List<LambdaExpression>();

                    returnConversions.Add(lambda);
                    // Change the Return Type to be whatever the conversion function returns
                    // for the next round of processing
                    returnType = lambda.ReturnType;
                    conversionsApplied.Add(convert);
                    applied = true;
                    break;
                }
                if (applied)
                    depth++;
                else
                    break; // None of the conversions were applied - stop trying
            } // while checking types

            return returnConversions;
        }

        // returnsConversion and the entries in paramsConversions may be null.
        static void ApplyConversions(ExcelFunctionRegistration reg, List<List<LambdaExpression>> paramsConversions, List<LambdaExpression> returnConversions)
        {
            // CAREFUL: The parameter transformations are applied in reverse order to how they're identified.
            // We do the following transformation
            //      public static string dnaParameterConvertTest(double? optTest) {   };
            //
            // with conversions convert1 and convert2 taking us from Type1 to double?
            // 
            // to
            //      public static string dnaParameterConvertTest(Type1 optTest) 
            //      {   
            //          return convertRet2(convertRet1(
            //                      dnaParameterConvertTest(
            //                          paramConvert1(optTest)
            //                            )));
            //      };
            // 
            // and then with a conversion from object to Type1, resulting in
            //
            //      public static string dnaParameterConvertTest(object optTest) 
            //      {   
            //          return convertRet2(convertRet1(
            //                      dnaParameterConvertTest(
            //                          paramConvert1(paramConvert2(optTest))
            //                            )));
            //      };

            Debug.Assert(reg.FunctionLambda.Parameters.Count == paramsConversions.Count);

            // NOTE: To cater for the Range COM type equivalance, we need to distinguish the FunctionLambda's parameter type and the paramConversion ReturnType.
            //       These need not be the same, but the should at least be equivalent.

            // build up the invoke expression for each parameter
            var wrappingParameters = reg.FunctionLambda.Parameters.Select(p => Expression.Parameter(p.Type, p.Name)).ToList();

            // Build the nested parameter convertion expression.
            // Start with the wrapping parameters as they are. Then replace with the nesting of conversions as needed.
            var paramExprs = new List<Expression>(wrappingParameters);
            for (int i = 0; i < paramsConversions.Count; i++)
            {
                var paramConversions = paramsConversions[i];
                if (paramConversions == null)
                    continue;

                // If we have a list, there should be at least one conversion in it.
                Debug.Assert(paramConversions.Count > 0);
                // Update the calling parameter type to be the outer one in the conversion chain.
                wrappingParameters[i] = Expr.Parameter(paramConversions.Last().Parameters[0].Type, wrappingParameters[i].Name);
                // Start with just the (now updated) outer param which will be the inner-most value in the conversion chain
                Expression wrappedExpr = wrappingParameters[i];
                // Need to go in reverse for the parameter wrapping
                // Need to now build from the inside out
                foreach (var conversion in Enumerable.Reverse(paramConversions))
                {
                    wrappedExpr = Expr.Invoke(conversion, wrappedExpr);
                }
                paramExprs[i] = wrappedExpr;
            }

            var wrappingCall = Expr.Invoke(reg.FunctionLambda, paramExprs);
            if (returnConversions != null)
            {
                foreach (var conversion in returnConversions)
                    wrappingCall = Expr.Invoke(conversion, wrappingCall);
            }

            reg.FunctionLambda = Expr.Lambda(wrappingCall, reg.FunctionLambda.Name, wrappingParameters);
        }
    }
}
