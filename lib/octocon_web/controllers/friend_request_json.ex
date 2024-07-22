defmodule OctoconWeb.FriendRequestJSON do
  def index(%{requests: %{incoming: incoming, outgoing: outgoing}}) do
    %{
      data: %{
        incoming:
          incoming
          |> Enum.map(fn %{request: request, from: from} ->
            %{
              request: %{
                date_sent: request.date_sent
              },
              system: %{
                id: from.id,
                username: from.username,
                discord_id: from.discord_id,
                avatar_url: from.avatar_url
              }
            }
          end),
        outgoing:
          outgoing
          |> Enum.map(fn %{request: request, to: to} ->
            %{
              request: %{
                date_sent: request.date_sent
              },
              system: %{
                id: to.id,
                username: to.username,
                discord_id: to.discord_id,
                avatar_url: to.avatar_url
              }
            }
          end)
      }
    }
  end

  def data(%{request: request, system: system}) do
    %{
      request: %{
        date_sent: request.date_sent
      },
      system: %{
        id: system.id,
        username: system.username,
        discord_id: system.discord_id,
        avatar_url: system.avatar_url
      }
    }
  end
end
