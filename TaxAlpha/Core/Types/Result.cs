using System;
using System.Collections.Generic;
using System.Text;

namespace TaxAlpha.Core.Types
{   
    public enum ErrorCode
    {
        RuntimeError,
        WrongArgument,
        LogicError,
        ComputationError
    }

    public record ErrorType(ErrorCode code, string message);

    public class Result<T>
    {
        public T? Value { get; }
        public bool IsSuccess { get; }
        public bool IsError => !IsSuccess;
        public ErrorType? Error {  get; }
                
        private Result(T value, bool isSuccess, ErrorType? error) {
            Value = value;
            IsSuccess = isSuccess;
            Error = error;
        }
        
        private Result(ErrorType error)
        {
            Value = default;
            IsSuccess = false;
            Error = error;
        }

        public static Result<T> Success(T value) {
            return new Result<T>(value: value,
                isSuccess: true, 
                error: null); }
        public static Result<T> Failure(string errorMessage)
        {
            return new Result<T>(error: new ErrorType(code: ErrorCode.RuntimeError, message: errorMessage));
        }

        public static Result<T> Failure(ErrorType error)
        {
            return new Result<T>(error: error);
        }
    }
}
