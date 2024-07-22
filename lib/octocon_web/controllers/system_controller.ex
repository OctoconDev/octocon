defmodule OctoconWeb.SystemController do
  use OctoconWeb, :controller

  import OctoconWeb.Utils.Systems

  action_fallback OctoconWeb.FallbackController

  def show(conn, %{"system_id" => id}) do
    case parse_system(conn, id) do
      {:noreply, conn} -> conn
      {:self, system} -> render(conn, :show_me, system: system)
      {:other, system} -> render(conn, :show, system: system)
    end
  end

  # def update(conn, %{"id" => id, "alter" => alter_params}) do
  #  alter = Alters.get_alter!(id)
  #
  #  with {:ok, %Alter{} = alter} <- Alters.update_alter(alter, alter_params) do
  #    render(conn, :show, alter: alter)
  #  end
  # end

  # def delete(conn, %{"id" => id}) do
  #  alter = Alters.get_alter!(id)
  #
  #  with {:ok, %Alter{}} <- Alters.delete_alter(alter) do
  #    send_resp(conn, :no_content, "")
  #  end
  # end
end
