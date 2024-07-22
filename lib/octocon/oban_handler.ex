defmodule Octocon.ObanHandler do
  @moduledoc """
  This module serves as a wrapper around Oban so that it will work properly
  with `Fly.Repo` instead of an `Ecto.Repo`.
  """

  for {func, arity} <- Oban.__info__(:functions), func not in [:child_spec, :init, :start_link] do
    args = Macro.generate_arguments(arity, __MODULE__)

    @doc """
    See documentation for Oban.#{func}/#{arity}
    """
    def unquote(func)(unquote_splicing(args)) do
      Fly.RPC.rpc_primary({Oban, unquote(func), unquote(args)})
    end
  end
end
