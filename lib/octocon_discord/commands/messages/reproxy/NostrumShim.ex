defmodule OctoconDiscord.Commands.Messages.Reproxy.NostrumShim do
  @moduledoc """
  Fixes Nostrum bug where `/messages` is left out of the path for PATCH requests by shimming part of Nostrum.Api's private API.
  """

  def edit_webhook_message(webhook_id, webhook_token, message_id, args) do
    Nostrum.Api.request(
      :patch,
      # Fix here:
      "/webhooks/#{webhook_id}/#{webhook_token}/messages/#{message_id}",
      combine_embeds(args) |> combine_files()
    )
    |> handle_request_with_decode({:struct, Nostrum.Struct.Message})
  end

  # If `:embed` is present, prepend to `:embeds` for compatibility
  defp combine_embeds(%{embed: embed} = args),
    do: Map.delete(args, :embed) |> Map.put(:embeds, [embed | args[:embeds] || []])

  defp combine_embeds(%{data: data} = args), do: %{args | data: combine_embeds(data)}
  defp combine_embeds(%{message: data} = args), do: %{args | message: combine_embeds(data)}
  defp combine_embeds(args), do: args

  # If `:file` is present, prepend to `:files` for compatibility
  defp combine_files(%{file: file} = args),
    do: Map.delete(args, :file) |> Map.put(:files, [file | args[:files] || []])

  defp combine_files(%{data: data} = args), do: %{args | data: combine_files(data)}
  defp combine_files(%{message: data} = args), do: %{args | message: combine_files(data)}
  defp combine_files(args), do: args

  # If `:file` is present, prepend to `:files` for compatibility
  defp combine_files(%{file: file} = args),
    do: Map.delete(args, :file) |> Map.put(:files, [file | args[:files] || []])

  defp combine_files(%{data: data} = args), do: %{args | data: combine_files(data)}
  defp combine_files(%{message: data} = args), do: %{args | message: combine_files(data)}
  defp combine_files(args), do: args

  defp handle_request_with_decode(response)
  defp handle_request_with_decode({:ok, body}), do: {:ok, Jason.decode!(body, keys: :atoms)}
  defp handle_request_with_decode({:error, _} = error), do: error

  defp handle_request_with_decode(response, type)
  # add_guild_member/3 can return both a 201 and a 204
  defp handle_request_with_decode({:ok}, _type), do: {:ok}
  defp handle_request_with_decode({:error, _} = error, _type), do: error

  defp handle_request_with_decode({:ok, body}, type) do
    convert =
      body
      |> Jason.decode!(keys: :atoms)
      |> Nostrum.Util.cast(type)

    {:ok, convert}
  end
end
